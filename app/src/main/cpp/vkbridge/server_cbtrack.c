/* SPDX-License-Identifier: GPL-3.0-or-later
 *
 * The command buffers each device has handed out, so that a handle the driver never gave (or has
 * since freed, with its pool) is refused here instead of reaching the driver. Handles arrive from
 * clients as plain numbers; one the driver did not know took the whole app down inside Mali's
 * vkBeginCommandBuffer (SIGSEGV at 0x10, Steam's Zink on an Edge 60 and a Tab S11). A refused
 * command buffer fails its call with VK_ERROR_DEVICE_LOST, which the client reports, and is logged
 * once per handle.
 */
#define _GNU_SOURCE
#include "vkb_server.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CBT_BUCKETS 1024

enum { CBT_INITIAL, CBT_RECORDING, CBT_EXECUTABLE };

typedef struct cbt_entry {
    VkCommandBuffer cb;
    VkCommandPool pool;
    VkCommandBufferLevel level;
    int state;
    VkCommandBufferUsageFlags begin_flags;
    int inheritance;
    uint32_t begins;
    struct cbt_entry *next;
} cbt_entry;

typedef struct cbt_pool {
    VkCommandPool pool;
    VkCommandPoolCreateFlags flags;
    uint32_t qfam;
    struct cbt_pool *next;
} cbt_pool;

typedef struct vkb_cbtrack {
    pthread_mutex_t lock;
    cbt_entry *b[CBT_BUCKETS];
    VkCommandBuffer last_refused;
    cbt_pool *pools;
    int warned_implicit_reset;
    PFN_vkCreateCommandPool create_pool;
    PFN_vkResetCommandPool reset_pool;
    PFN_vkResetCommandBuffer reset_cb;
    PFN_vkAllocateCommandBuffers alloc;
    PFN_vkFreeCommandBuffers free_cbs;
    PFN_vkDestroyCommandPool destroy_pool;
    PFN_vkBeginCommandBuffer begin;
    PFN_vkQueueSubmit submit;
    PFN_vkQueueSubmit2 submit2;
} vkb_cbtrack;

static unsigned bucket(VkCommandBuffer cb)
{
    uint64_t k = (uint64_t)(uintptr_t)cb;
    k ^= k >> 31;
    k *= 0x9e3779b97f4a7c15ull;
    return (unsigned)(k >> 54) & (CBT_BUCKETS - 1);
}

static vkb_cbtrack *cur(void)
{
    vkb_srv_table *t = vkb_srv_current_table();
    return t ? t->cbtrack : NULL;
}

static cbt_entry *find_locked(vkb_cbtrack *k, VkCommandBuffer cb)
{
    for (cbt_entry *e = k->b[bucket(cb)]; e; e = e->next)
        if (e->cb == cb) return e;
    return NULL;
}

static int known_locked(vkb_cbtrack *k, VkCommandBuffer cb) { return find_locked(k, cb) != NULL; }

static cbt_pool *pool_locked(vkb_cbtrack *k, VkCommandPool pool)
{
    for (cbt_pool *p = k->pools; p; p = p->next)
        if (p->pool == pool) return p;
    return NULL;
}

static const char *state_name(int s) { return s == CBT_RECORDING ? "recording" : s == CBT_EXECUTABLE ? "executable" : "initial"; }

void vkb_cbtrack_describe(vkb_srv_table *t, VkCommandBuffer cb, char *out, size_t n)
{
    vkb_cbtrack *k = t ? t->cbtrack : NULL;
    if (!k) {
        snprintf(out, n, "command buffer %p (not tracked)", (void *)cb);
        return;
    }
    pthread_mutex_lock(&k->lock);
    cbt_entry *e = find_locked(k, cb);
    cbt_pool *p = e ? pool_locked(k, e->pool) : NULL;
    if (!e)
        snprintf(out, n, "command buffer %p: unknown to this device", (void *)cb);
    else
        snprintf(out, n, "command buffer %p: level %s, state %s, begin #%u flags 0x%x%s; pool %p flags %s (queue family %d)",
                 (void *)cb, e->level == VK_COMMAND_BUFFER_LEVEL_SECONDARY ? "secondary" : "primary", state_name(e->state),
                 e->begins, (unsigned)e->begin_flags, e->inheritance ? " with inheritance info" : "", (void *)e->pool,
                 p ? (p->flags & VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT ? "RESET_COMMAND_BUFFER" : "0") : "unknown",
                 p ? (int)p->qfam : -1);
    if (p && (p->flags & ~VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT))
        snprintf(out + strlen(out), n - strlen(out), " +0x%x", (unsigned)(p->flags & ~VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT));
    pthread_mutex_unlock(&k->lock);
}

void vkb_cbtrack_ended(vkb_srv_table *t, VkCommandBuffer cb)
{
    vkb_cbtrack *k = t ? t->cbtrack : NULL;
    if (!k) return;
    pthread_mutex_lock(&k->lock);
    cbt_entry *e = find_locked(k, cb);
    if (e) e->state = CBT_EXECUTABLE;
    pthread_mutex_unlock(&k->lock);
}

int vkb_cbtrack_known(vkb_srv_table *t, VkCommandBuffer cb, const char *what)
{
    vkb_cbtrack *k = t ? t->cbtrack : NULL;
    if (!k) return cb != VK_NULL_HANDLE;
    pthread_mutex_lock(&k->lock);
    int ok = cb && known_locked(k, cb);
    int first = !ok && k->last_refused != cb;
    if (!ok) k->last_refused = cb;
    pthread_mutex_unlock(&k->lock);
    if (first)
        VKB_ERR("device %u: %s on command buffer %p, which this device never allocated or has freed; refused (the client "
                "gets VK_ERROR_DEVICE_LOST)", t->id, what, (void *)cb);
    return ok;
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_AllocateCommandBuffers(VkDevice device, const VkCommandBufferAllocateInfo *info,
                                                                 VkCommandBuffer *out)
{
    vkb_cbtrack *k = cur();
    VkResult r = k->alloc(device, info, out);
    if (r != VK_SUCCESS || !info) return r;
    pthread_mutex_lock(&k->lock);
    for (uint32_t i = 0; i < info->commandBufferCount; i++) {
        if (!out[i] || known_locked(k, out[i])) continue;
        cbt_entry *e = malloc(sizeof(*e));
        if (!e) continue;
        memset(e, 0, sizeof(*e));
        e->cb = out[i];
        e->pool = info->commandPool;
        e->level = info->level;
        unsigned h = bucket(out[i]);
        e->next = k->b[h];
        k->b[h] = e;
    }
    pthread_mutex_unlock(&k->lock);
    return r;
}

static void forget_locked(vkb_cbtrack *k, VkCommandBuffer cb, VkCommandPool pool)
{
    for (unsigned h = cb ? bucket(cb) : 0; h < CBT_BUCKETS; h = cb ? CBT_BUCKETS : h + 1) {
        cbt_entry **pp = &k->b[h];
        while (*pp) {
            cbt_entry *e = *pp;
            if ((cb && e->cb == cb) || (!cb && e->pool == pool)) {
                *pp = e->next;
                free(e);
            } else {
                pp = &e->next;
            }
        }
    }
}

static VKAPI_ATTR void VKAPI_CALL cbt_FreeCommandBuffers(VkDevice device, VkCommandPool pool, uint32_t n, const VkCommandBuffer *cbs)
{
    vkb_cbtrack *k = cur();
    pthread_mutex_lock(&k->lock);
    for (uint32_t i = 0; i < n; i++)
        if (cbs[i]) forget_locked(k, cbs[i], pool);
    pthread_mutex_unlock(&k->lock);
    k->free_cbs(device, pool, n, cbs);
}

static VKAPI_ATTR void VKAPI_CALL cbt_DestroyCommandPool(VkDevice device, VkCommandPool pool, const VkAllocationCallbacks *a)
{
    vkb_cbtrack *k = cur();
    if (pool) {
        pthread_mutex_lock(&k->lock);
        forget_locked(k, VK_NULL_HANDLE, pool);
        for (cbt_pool **pp = &k->pools; *pp; pp = &(*pp)->next) {
            if ((*pp)->pool == pool) {
                cbt_pool *p = *pp;
                *pp = p->next;
                free(p);
                break;
            }
        }
        pthread_mutex_unlock(&k->lock);
    }
    k->destroy_pool(device, pool, a);
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_CreateCommandPool(VkDevice device, const VkCommandPoolCreateInfo *info,
                                                            const VkAllocationCallbacks *a, VkCommandPool *out)
{
    vkb_cbtrack *k = cur();
    VkResult r = k->create_pool(device, info, a, out);
    if (r != VK_SUCCESS || !info || !out) return r;
    cbt_pool *p = calloc(1, sizeof(*p));
    if (!p) return r;
    p->pool = *out;
    p->flags = info->flags;
    p->qfam = info->queueFamilyIndex;
    pthread_mutex_lock(&k->lock);
    p->next = k->pools;
    k->pools = p;
    pthread_mutex_unlock(&k->lock);
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_ResetCommandPool(VkDevice device, VkCommandPool pool, VkCommandPoolResetFlags flags)
{
    vkb_cbtrack *k = cur();
    pthread_mutex_lock(&k->lock);
    for (unsigned h = 0; h < CBT_BUCKETS; h++)
        for (cbt_entry *e = k->b[h]; e; e = e->next)
            if (e->pool == pool) e->state = CBT_INITIAL;
    pthread_mutex_unlock(&k->lock);
    return k->reset_pool(device, pool, flags);
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_ResetCommandBuffer(VkCommandBuffer cb, VkCommandBufferResetFlags flags)
{
    vkb_cbtrack *k = cur();
    if (!vkb_cbtrack_known(vkb_srv_current_table(), cb, "vkResetCommandBuffer")) return VK_ERROR_DEVICE_LOST;
    pthread_mutex_lock(&k->lock);
    cbt_entry *e = find_locked(k, cb);
    if (e) e->state = CBT_INITIAL;
    pthread_mutex_unlock(&k->lock);
    return k->reset_cb(cb, flags);
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_BeginCommandBuffer(VkCommandBuffer cb, const VkCommandBufferBeginInfo *info)
{
    vkb_cbtrack *k = cur();
    vkb_srv_table *t = vkb_srv_current_table();
    if (!vkb_cbtrack_known(t, cb, "vkBeginCommandBuffer")) return VK_ERROR_DEVICE_LOST;
    pthread_mutex_lock(&k->lock);
    cbt_entry *e = find_locked(k, cb);
    int bad_reset = 0, prev = CBT_INITIAL;
    if (e) {
        cbt_pool *p = pool_locked(k, e->pool);
        prev = e->state;
        bad_reset = prev != CBT_INITIAL && p && !(p->flags & VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT) && !k->warned_implicit_reset;
        if (bad_reset) k->warned_implicit_reset = 1;
        e->state = CBT_RECORDING;
        e->begins++;
        e->begin_flags = info ? info->flags : 0;
        e->inheritance = info && info->pInheritanceInfo;
    }
    pthread_mutex_unlock(&k->lock);
    if (bad_reset)
        VKB_WARN("device %u: vkBeginCommandBuffer on command buffer %p in the %s state, from a pool without "
                 "RESET_COMMAND_BUFFER_BIT (an implicit reset the spec does not allow; logged once)", t ? t->id : 0, (void *)cb,
                 state_name(prev));
    return k->begin(cb, info);
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_QueueSubmit(VkQueue queue, uint32_t n, const VkSubmitInfo *s, VkFence fence)
{
    vkb_cbtrack *k = cur();
    vkb_srv_table *t = vkb_srv_current_table();
    for (uint32_t i = 0; i < n; i++)
        for (uint32_t j = 0; j < s[i].commandBufferCount; j++)
            if (!vkb_cbtrack_known(t, s[i].pCommandBuffers[j], "vkQueueSubmit")) return VK_ERROR_DEVICE_LOST;
    return k->submit(queue, n, s, fence);
}

static VKAPI_ATTR VkResult VKAPI_CALL cbt_QueueSubmit2(VkQueue queue, uint32_t n, const VkSubmitInfo2 *s, VkFence fence)
{
    vkb_cbtrack *k = cur();
    vkb_srv_table *t = vkb_srv_current_table();
    for (uint32_t i = 0; i < n; i++)
        for (uint32_t j = 0; j < s[i].commandBufferInfoCount; j++)
            if (!vkb_cbtrack_known(t, s[i].pCommandBufferInfos[j].commandBuffer, "vkQueueSubmit2")) return VK_ERROR_DEVICE_LOST;
    return k->submit2(queue, n, s, fence);
}

void vkb_cbtrack_device_init(vkb_srv_table *t)
{
    if (!t->dt.vkAllocateCommandBuffers || !t->dt.vkFreeCommandBuffers || !t->dt.vkDestroyCommandPool || !t->dt.vkBeginCommandBuffer ||
        !t->dt.vkQueueSubmit)
        return;
    vkb_cbtrack *k = calloc(1, sizeof(*k));
    if (!k) return;
    pthread_mutex_init(&k->lock, NULL);
    k->create_pool = t->dt.vkCreateCommandPool;
    k->reset_pool = t->dt.vkResetCommandPool;
    k->reset_cb = t->dt.vkResetCommandBuffer;
    k->alloc = t->dt.vkAllocateCommandBuffers;
    k->free_cbs = t->dt.vkFreeCommandBuffers;
    k->destroy_pool = t->dt.vkDestroyCommandPool;
    k->begin = t->dt.vkBeginCommandBuffer;
    k->submit = t->dt.vkQueueSubmit;
    k->submit2 = t->dt.vkQueueSubmit2;
    t->cbtrack = k;
    t->dt.vkAllocateCommandBuffers = cbt_AllocateCommandBuffers;
    t->dt.vkFreeCommandBuffers = cbt_FreeCommandBuffers;
    t->dt.vkDestroyCommandPool = cbt_DestroyCommandPool;
    t->dt.vkBeginCommandBuffer = cbt_BeginCommandBuffer;
    t->dt.vkQueueSubmit = cbt_QueueSubmit;
    if (k->submit2) t->dt.vkQueueSubmit2 = cbt_QueueSubmit2;
    if (k->create_pool) t->dt.vkCreateCommandPool = cbt_CreateCommandPool;
    if (k->reset_pool) t->dt.vkResetCommandPool = cbt_ResetCommandPool;
    if (k->reset_cb) t->dt.vkResetCommandBuffer = cbt_ResetCommandBuffer;
}

void vkb_cbtrack_device_destroy(vkb_srv_table *t)
{
    vkb_cbtrack *k = t->cbtrack;
    if (!k) return;
    for (unsigned h = 0; h < CBT_BUCKETS; h++) {
        while (k->b[h]) {
            cbt_entry *e = k->b[h];
            k->b[h] = e->next;
            free(e);
        }
    }
    while (k->pools) {
        cbt_pool *p = k->pools;
        k->pools = p->next;
        free(p);
    }
    pthread_mutex_destroy(&k->lock);
    free(k);
    t->cbtrack = NULL;
}
