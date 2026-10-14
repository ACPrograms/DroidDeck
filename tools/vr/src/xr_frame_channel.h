#pragma once
#include "xr_frame.h"
#include <atomic>
#include <cerrno>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <poll.h>
#include <fcntl.h>
#include <sys/file.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

inline int64_t xr_frame_now() {
    timespec now{}; clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000LL + now.tv_nsec;
}

inline bool xr_frame_io(int descriptor, void *data, size_t size, bool write, int64_t deadline) {
    auto *bytes = static_cast<unsigned char *>(data);
    while (size) {
        int64_t remaining = deadline - xr_frame_now();
        if (remaining <= 0) return false;
        pollfd ready{descriptor, (short)(write ? POLLOUT : POLLIN), 0};
        int result = poll(&ready, 1, (int)((remaining + 999999) / 1000000));
        if (result < 0 && errno == EINTR) continue;
        if (result <= 0 || !(ready.revents & ready.events)) return false;
        ssize_t done = write ? send(descriptor, bytes, size, MSG_NOSIGNAL | MSG_DONTWAIT)
                             : recv(descriptor, bytes, size, MSG_DONTWAIT);
        if (done < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK)) continue;
        if (done <= 0) return false;
        bytes += done; size -= done;
    }
    return true;
}

inline bool xr_frame_peer(int descriptor) {
    struct { pid_t pid; uid_t uid; gid_t gid; } peer{};
    socklen_t size = sizeof(peer);
    return getsockopt(descriptor, SOL_SOCKET, SO_PEERCRED, &peer, &size) == 0
        && size == sizeof(peer) && peer.uid == getuid();
}

struct DroidDeckXrPixels {
    DroidDeckXrFrameHeader header{};
    std::vector<unsigned char> pixels;
};

class XrFrameReceiver {
public:
    ~XrFrameReceiver() { Stop(); }
    bool Start(const std::string &filename) {
        if (listener_ >= 0 || filename.empty() || filename.size() >= sizeof(sockaddr_un::sun_path)) return false;
        lock_ = open((filename + ".lock").c_str(), O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
        struct stat status{};
        if (lock_ < 0) return false;
        if (fstat(lock_, &status) != 0 || !S_ISREG(status.st_mode) || status.st_uid != getuid()
            || flock(lock_, LOCK_EX | LOCK_NB) != 0) { close(lock_); lock_ = -1; return false; }
        if (lstat(filename.c_str(), &status) == 0) {
            if (!S_ISSOCK(status.st_mode) || status.st_uid != getuid() || unlink(filename.c_str()) != 0) {
                close(lock_); lock_ = -1; return false;
            }
        } else if (errno != ENOENT) { close(lock_); lock_ = -1; return false; }
        sockaddr_un address{}; address.sun_family = AF_UNIX;
        std::strcpy(address.sun_path, filename.c_str());
        listener_ = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
        if (listener_ < 0) { Stop(); return false; }
        if (bind(listener_, reinterpret_cast<sockaddr *>(&address), sizeof(address)) != 0) { Stop(); return false; }
        filename_ = filename;
        if (chmod(filename.c_str(), 0600) != 0 || listen(listener_, 2) != 0) { Stop(); return false; }
        { std::lock_guard<std::mutex> lock(mutex_); pending_ = {}; }
        received_ = rejected_ = 0;
        stopping_ = false;
        thread_ = std::thread([this] { Loop(); });
        return true;
    }
    void Stop() {
        stopping_ = true;
        if (thread_.joinable()) thread_.join();
        if (listener_ >= 0) close(listener_);
        listener_ = -1;
        if (!filename_.empty()) unlink(filename_.c_str());
        filename_.clear();
        if (lock_ >= 0) close(lock_);
        lock_ = -1;
    }
    bool Take(DroidDeckXrPixels *out) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (pending_.pixels.empty()) return false;
        *out = std::move(pending_);
        pending_ = {};
        return true;
    }
    uint64_t Rejected() const { return rejected_; }
    uint64_t Received() const { return received_; }
private:
    int listener_ = -1;
    int lock_ = -1;
    std::string filename_;
    std::atomic<bool> stopping_{false};
    std::atomic<uint64_t> rejected_{0}, received_{0};
    std::thread thread_;
    std::mutex mutex_;
    DroidDeckXrPixels pending_;
    void Loop() {
        while (!stopping_) {
            pollfd ready{listener_, POLLIN, 0};
            if (poll(&ready, 1, 50) <= 0) continue;
            int client = accept4(listener_, nullptr, nullptr, SOCK_CLOEXEC | SOCK_NONBLOCK);
            if (client < 0) continue;
            if (!xr_frame_peer(client)) { rejected_++; close(client); continue; }
            uint64_t lastFrame = 0;
            while (!stopping_) {
                DroidDeckXrPixels frame;
                int64_t deadline = xr_frame_now() + 200000000LL;
                if (!xr_frame_io(client, &frame.header, sizeof(frame.header), false, deadline)) break;
                int64_t now = xr_frame_now();
                if (!xr_frame_valid(frame.header) || frame.header.captured_ns > now
                    || now - frame.header.captured_ns > 500000000LL || frame.header.frame_id <= lastFrame) { rejected_++; break; }
                try { frame.pixels.resize(frame.header.bytes); }
                catch (...) { rejected_++; break; }
                if (!xr_frame_io(client, frame.pixels.data(), frame.pixels.size(), false, deadline)) { rejected_++; break; }
                if (xr_frame_now() - frame.header.captured_ns > 500000000LL) { rejected_++; break; }
                uint64_t acknowledgement = frame.header.frame_id;
                lastFrame = acknowledgement;
                {
                    std::lock_guard<std::mutex> lock(mutex_);
                    pending_ = std::move(frame);
                }
                received_++;
                if (!xr_frame_io(client, &acknowledgement, sizeof(acknowledgement), true, deadline)) break;
            }
            close(client);
        }
    }
};

class XrFrameSender {
public:
    XrFrameSender() = default;
    XrFrameSender(const XrFrameSender &) = delete;
    XrFrameSender &operator=(const XrFrameSender &) = delete;
    ~XrFrameSender() { Disconnect(); }
    bool Send(const std::string &filename, const DroidDeckXrFrameHeader &header, const unsigned char *pixels) {
        if (!pixels || !xr_frame_valid(header) || filename.empty() || filename.size() >= sizeof(sockaddr_un::sun_path)) return false;
        if (filename_ != filename) Disconnect();
        int64_t deadline = xr_frame_now() + 200000000LL;
        if (descriptor_ < 0) {
            sockaddr_un address{}; address.sun_family = AF_UNIX;
            std::strcpy(address.sun_path, filename.c_str());
            descriptor_ = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
            if (descriptor_ < 0) return false;
            int result = connect(descriptor_, reinterpret_cast<sockaddr *>(&address), sizeof(address));
            if (result != 0 && errno == EINPROGRESS) {
                pollfd ready{descriptor_, POLLOUT, 0};
                int error = 0; socklen_t size = sizeof(error);
                result = poll(&ready, 1, 200) > 0 && getsockopt(descriptor_, SOL_SOCKET, SO_ERROR, &error, &size) == 0
                    && error == 0 ? 0 : -1;
            }
            if (result != 0 || !xr_frame_peer(descriptor_)) {
                Disconnect(); return false;
            }
            filename_ = filename;
        }
        uint64_t acknowledgement = 0;
        if (!xr_frame_io(descriptor_, const_cast<DroidDeckXrFrameHeader *>(&header), sizeof(header), true, deadline)
            || !xr_frame_io(descriptor_, const_cast<unsigned char *>(pixels), header.bytes, true, deadline)
            || !xr_frame_io(descriptor_, &acknowledgement, sizeof(acknowledgement), false, deadline)
            || acknowledgement != header.frame_id) { Disconnect(); return false; }
        return true;
    }
private:
    int descriptor_ = -1;
    std::string filename_;
    void Disconnect() { if (descriptor_ >= 0) close(descriptor_); descriptor_ = -1; filename_.clear(); }
};
