#define _GNU_SOURCE
#include <assert.h>
#include <errno.h>
#include <linux/futex.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <sys/syscall.h>
#include <unistd.h>

int bl_fsync_syscall(long number, const long *args, long *result) {
  (void)number;
  (void)args;
  (void)result;
  return 0;
}

#include "../linuxfs/preload/robust.c"

static long (*kernel_syscall)(long, ...);
static int missing_error;

static long missing_robust(long number, ...) {
  assert(number == SYS_get_robust_list);
  errno = missing_error;
  return -1;
}

static struct robust_list_head *check_head(void) {
  struct robust_list_head *expected = NULL;
  size_t expected_length = 0;
  assert(kernel_syscall(SYS_get_robust_list, 0, &expected, &expected_length) == 0);
  struct robust_list_head *head = NULL;
  size_t length = 0;
  errno = E2BIG;
  assert(syscall(SYS_get_robust_list, 0L, &head, &length, 0L, 0L, 0L) == 0);
  assert(errno == E2BIG);
  assert(head == expected && length == expected_length);
  return head;
}

static void *check_thread(void *argument) {
  (void)argument;
  pthread_mutexattr_t attributes;
  pthread_mutex_t mutex;
  assert(pthread_mutexattr_init(&attributes) == 0);
  assert(pthread_mutexattr_setrobust(&attributes, PTHREAD_MUTEX_ROBUST) == 0);
  assert(pthread_mutex_init(&mutex, &attributes) == 0);
  assert(pthread_mutexattr_destroy(&attributes) == 0);
  struct robust_list_head *head = check_head();
  assert(pthread_mutex_lock(&mutex) == 0);
  struct robust_list *first = head->list.next;
  struct robust_list *pending = head->list_op_pending;
  assert(check_head() == head);
  assert(head->list.next == first && head->list_op_pending == pending);
  assert(pthread_mutex_unlock(&mutex) == 0);
  assert(pthread_mutex_destroy(&mutex) == 0);
  return head;
}

int main(void) {
  kernel_syscall = dlsym(RTLD_NEXT, "syscall");
  assert(kernel_syscall);
  real_syscall = kernel_syscall;
  struct robust_list_head *main_head = check_thread(NULL);
  pthread_t thread;
  void *thread_head;
  assert(pthread_create(&thread, NULL, check_thread, NULL) == 0);
  assert(pthread_join(thread, &thread_head) == 0);
  assert(thread_head != main_head);
  real_syscall = missing_robust;
  missing_error = ENOSYS;
  check_thread(NULL);
  assert(pthread_create(&thread, NULL, check_thread, NULL) == 0);
  assert(pthread_join(thread, NULL) == 0);
  struct robust_list_head *head = NULL;
  size_t length = 0;
  assert(syscall(SYS_get_robust_list, 0L, NULL, &length, 0L, 0L, 0L) == -1);
  assert(errno == EFAULT);
  assert(syscall(SYS_get_robust_list, 0L, &head, NULL, 0L, 0L, 0L) == -1);
  assert(errno == EFAULT);
  head = NULL;
  missing_error = EPERM;
  assert(syscall(SYS_get_robust_list, 0L, &head, &length, 0L, 0L, 0L) == -1);
  assert(errno == EPERM && !head && !length);
  missing_error = EACCES;
  assert(syscall(SYS_get_robust_list, 0L, &head, &length, 0L, 0L, 0L) == -1);
  assert(errno == EACCES && !head && !length);
  puts("robust preload: native and ENOSYS fallback metadata, threads, errno and permission failures passed");
  return 0;
}
