#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <cstdio>
#include <dlfcn.h>
#include <vector>
#include <linux/sync_file.h>
#include <poll.h>
#include <sys/ioctl.h>
#include <unistd.h>

int main() {
    void *library = dlopen("libvulkan.so.1", RTLD_NOW);
    if (!library) { std::puts(dlerror()); return 1; }
    auto get = reinterpret_cast<PFN_vkGetInstanceProcAddr>(dlsym(library, "vkGetInstanceProcAddr"));
    auto create = reinterpret_cast<PFN_vkCreateInstance>(get(VK_NULL_HANDLE, "vkCreateInstance"));
    VkApplicationInfo application{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    application.pApplicationName = "DroidDeck sync capability probe";
    application.apiVersion = VK_API_VERSION_1_2;
    VkInstanceCreateInfo info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO}; info.pApplicationInfo = &application;
    VkInstance instance = VK_NULL_HANDLE;
    VkResult result = create(&info, nullptr, &instance);
    std::printf("create_instance=%d\n", result);
    if (result != VK_SUCCESS) return 1;
    auto enumerate = reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(get(instance, "vkEnumeratePhysicalDevices"));
    uint32_t count = 0;
    result = enumerate(instance, &count, nullptr);
    std::printf("enumerate=%d devices=%u\n", result, count);
    if (result != VK_SUCCESS || !count) return 1;
    std::vector<VkPhysicalDevice> devices(count);
    if (enumerate(instance, &count, devices.data()) != VK_SUCCESS) return 1;
    auto fenceProperties = reinterpret_cast<PFN_vkGetPhysicalDeviceExternalFenceProperties>(get(instance, "vkGetPhysicalDeviceExternalFenceProperties"));
    auto semaphoreProperties = reinterpret_cast<PFN_vkGetPhysicalDeviceExternalSemaphoreProperties>(get(instance, "vkGetPhysicalDeviceExternalSemaphoreProperties"));
    for (auto handleType : {VK_EXTERNAL_FENCE_HANDLE_TYPE_OPAQUE_FD_BIT, VK_EXTERNAL_FENCE_HANDLE_TYPE_SYNC_FD_BIT}) {
        VkPhysicalDeviceExternalFenceInfo query{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_FENCE_INFO};
        query.handleType = handleType;
        VkExternalFenceProperties properties{VK_STRUCTURE_TYPE_EXTERNAL_FENCE_PROPERTIES};
        fenceProperties(devices[0], &query, &properties);
        std::printf("fence type=%u features=%u compatible=%u\n", handleType, properties.externalFenceFeatures, properties.compatibleHandleTypes);
    }
    for (auto handleType : {VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT, VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT}) {
        VkPhysicalDeviceExternalSemaphoreInfo query{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_SEMAPHORE_INFO};
        query.handleType = handleType;
        VkExternalSemaphoreProperties properties{VK_STRUCTURE_TYPE_EXTERNAL_SEMAPHORE_PROPERTIES};
        semaphoreProperties(devices[0], &query, &properties);
        std::printf("semaphore type=%u features=%u compatible=%u\n", handleType, properties.externalSemaphoreFeatures, properties.compatibleHandleTypes);
    }
    auto check = [](VkResult status, const char *step) {
        if (status != VK_SUCCESS) std::printf("%s=%d\n", step, status);
        return status == VK_SUCCESS;
    };
    auto createDevice = reinterpret_cast<PFN_vkCreateDevice>(get(instance, "vkCreateDevice"));
    float priority = 1;
    VkDeviceQueueCreateInfo queueInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    queueInfo.queueFamilyIndex = 0; queueInfo.queueCount = 1; queueInfo.pQueuePriorities = &priority;
    const char *extensions[] = {VK_KHR_EXTERNAL_FENCE_FD_EXTENSION_NAME};
    VkDeviceCreateInfo deviceInfo{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    deviceInfo.queueCreateInfoCount = 1; deviceInfo.pQueueCreateInfos = &queueInfo;
    deviceInfo.enabledExtensionCount = 1; deviceInfo.ppEnabledExtensionNames = extensions;
    VkDevice device = VK_NULL_HANDLE;
    if (!check(createDevice(devices[0], &deviceInfo, nullptr, &device), "create_device")) return 1;
    auto getDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(get(instance, "vkGetDeviceProcAddr"));
#define DEVICE_PROC(name) auto name = reinterpret_cast<PFN_vk##name>(getDeviceProcAddr(device, "vk" #name))
    DEVICE_PROC(GetDeviceQueue); DEVICE_PROC(CreateBuffer); DEVICE_PROC(GetBufferMemoryRequirements);
    DEVICE_PROC(AllocateMemory); DEVICE_PROC(BindBufferMemory); DEVICE_PROC(MapMemory);
    DEVICE_PROC(CreateCommandPool); DEVICE_PROC(AllocateCommandBuffers); DEVICE_PROC(BeginCommandBuffer);
    DEVICE_PROC(CmdFillBuffer); DEVICE_PROC(CmdPipelineBarrier); DEVICE_PROC(EndCommandBuffer);
    DEVICE_PROC(CreateFence); DEVICE_PROC(QueueSubmit); DEVICE_PROC(GetFenceFdKHR); DEVICE_PROC(QueueWaitIdle);
    DEVICE_PROC(InvalidateMappedMemoryRanges); DEVICE_PROC(UnmapMemory); DEVICE_PROC(DestroyFence);
    DEVICE_PROC(DestroyCommandPool); DEVICE_PROC(DestroyBuffer); DEVICE_PROC(FreeMemory); DEVICE_PROC(DestroyDevice);
    VkQueue queue = VK_NULL_HANDLE; GetDeviceQueue(device, 0, 0, &queue);
    VkBufferCreateInfo bufferInfo{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    bufferInfo.size = 4096; bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    VkBuffer buffer = VK_NULL_HANDLE;
    if (!check(CreateBuffer(device, &bufferInfo, nullptr, &buffer), "create_buffer")) return 1;
    VkMemoryRequirements requirements{}; GetBufferMemoryRequirements(device, buffer, &requirements);
    auto getMemoryProperties = reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(get(instance, "vkGetPhysicalDeviceMemoryProperties"));
    VkPhysicalDeviceMemoryProperties memoryProperties{}; getMemoryProperties(devices[0], &memoryProperties);
    uint32_t memoryType = UINT32_MAX;
    for (uint32_t index = 0; index < memoryProperties.memoryTypeCount; index++) {
        if ((requirements.memoryTypeBits & (1u << index)) &&
            (memoryProperties.memoryTypes[index].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)) {
            memoryType = index; break;
        }
    }
    if (memoryType == UINT32_MAX) return 1;
    VkMemoryAllocateInfo allocation{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    allocation.allocationSize = requirements.size; allocation.memoryTypeIndex = memoryType;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    if (!check(AllocateMemory(device, &allocation, nullptr, &memory), "allocate_memory")) return 1;
    if (!check(BindBufferMemory(device, buffer, memory, 0), "bind_buffer")) return 1;
    VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO}; poolInfo.queueFamilyIndex = 0;
    VkCommandPool pool = VK_NULL_HANDLE;
    if (!check(CreateCommandPool(device, &poolInfo, nullptr, &pool), "create_pool")) return 1;
    VkCommandBufferAllocateInfo commandInfo{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    commandInfo.commandPool = pool; commandInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; commandInfo.commandBufferCount = 1;
    VkCommandBuffer command = VK_NULL_HANDLE;
    if (!check(AllocateCommandBuffers(device, &commandInfo, &command), "allocate_command")) return 1;
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    if (!check(BeginCommandBuffer(command, &begin), "begin_command")) return 1;
    CmdFillBuffer(command, buffer, 0, 4096, 0x13579bdf);
    VkMemoryBarrier barrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; barrier.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
    CmdPipelineBarrier(command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &barrier, 0, nullptr, 0, nullptr);
    if (!check(EndCommandBuffer(command), "end_command")) return 1;
    VkExportFenceCreateInfo exportInfo{VK_STRUCTURE_TYPE_EXPORT_FENCE_CREATE_INFO};
    exportInfo.handleTypes = VK_EXTERNAL_FENCE_HANDLE_TYPE_SYNC_FD_BIT;
    VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO}; fenceInfo.pNext = &exportInfo;
    VkFence fence = VK_NULL_HANDLE;
    if (!check(CreateFence(device, &fenceInfo, nullptr, &fence), "create_fence")) return 1;
    VkSubmitInfo submission{VK_STRUCTURE_TYPE_SUBMIT_INFO}; submission.commandBufferCount = 1; submission.pCommandBuffers = &command;
    if (!check(QueueSubmit(queue, 1, &submission, fence), "submit")) return 1;
    VkFenceGetFdInfoKHR exportFd{VK_STRUCTURE_TYPE_FENCE_GET_FD_INFO_KHR};
    exportFd.fence = fence; exportFd.handleType = VK_EXTERNAL_FENCE_HANDLE_TYPE_SYNC_FD_BIT;
    int descriptor = -2;
    if (!check(GetFenceFdKHR(device, &exportFd, &descriptor), "export_sync_fd")) return 1;
    std::printf("export_sync_fd=%d\n", descriptor);
    if (descriptor < -1) return 1;
    if (descriptor >= 0) {
        sync_file_info syncInfo{};
        int infoResult = ioctl(descriptor, SYNC_IOC_FILE_INFO, &syncInfo);
        std::printf("sync_file_info=%d fences=%u status=%d\n", infoResult, syncInfo.num_fences, syncInfo.status);
        if (infoResult != 0 || syncInfo.num_fences == 0) { close(descriptor); return 1; }
        pollfd signal{descriptor, POLLIN, 0};
        int waited = poll(&signal, 1, 1000);
        std::printf("poll=%d events=%u\n", waited, signal.revents);
        close(descriptor);
        if (waited != 1 || !(signal.revents & POLLIN) || (signal.revents & POLLERR)) return 1;
    }
    if (!check(QueueWaitIdle(queue), "queue_idle")) return 1;
    VkMappedMemoryRange mappedRange{VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE};
    mappedRange.memory = memory; mappedRange.size = VK_WHOLE_SIZE;
    void *mapping = nullptr;
    if (!check(MapMemory(device, memory, 0, VK_WHOLE_SIZE, 0, &mapping), "map_memory")) return 1;
    if (!check(InvalidateMappedMemoryRanges(device, 1, &mappedRange), "invalidate_memory")) return 1;
    uint32_t matches = 0;
    for (uint32_t index = 0; index < 1024; index++) matches += static_cast<uint32_t *>(mapping)[index] == 0x13579bdf;
    std::printf("gpu_pattern_matches=%u/1024\n", matches);
    UnmapMemory(device, memory); DestroyFence(device, fence, nullptr);
    DestroyCommandPool(device, pool, nullptr); DestroyBuffer(device, buffer, nullptr);
    FreeMemory(device, memory, nullptr); DestroyDevice(device, nullptr);
    auto destroy = reinterpret_cast<PFN_vkDestroyInstance>(get(instance, "vkDestroyInstance"));
    destroy(instance, nullptr);
    return matches == 1024 ? 0 : 1;
}
