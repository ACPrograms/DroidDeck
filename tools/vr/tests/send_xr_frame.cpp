#include "../src/xr_frame_channel.h"
#include <cstdio>
#include <cstdlib>

int main(int argc, char **argv) {
    if (argc != 2) { std::fprintf(stderr, "usage: send_xr_frame <socket>\n"); return 2; }
    DroidDeckXrFrameHeader header{};
    header.magic = DROIDDECK_XR_FRAME_MAGIC; header.version = DROIDDECK_XR_FRAME_VERSION;
    header.width = 64; header.height = 32; header.bytes = header.width * header.height * 4; header.color_space = 1;
    for (int eye = 0; eye < 2; eye++) {
        for (int axis = 0; axis < 3; axis++) header.eye_pose[eye][axis][axis] = 1;
        header.eye_pose[eye][0][3] = eye == 0 ? -0.032f : 0.032f; header.eye_pose[eye][1][3] = 1.6f;
        header.projection[eye][0][0] = header.projection[eye][1][1] = 1;
        header.projection[eye][2][2] = -1.002f; header.projection[eye][2][3] = -0.1001f;
        header.projection[eye][3][2] = -1;
    }
    std::vector<unsigned char> pixels(header.bytes);
    for (uint32_t row = 0; row < header.height; row++) for (uint32_t column = 0; column < header.width; column++) {
        size_t offset = (row * header.width + column) * 4;
        pixels[offset] = column < header.width / 2 ? 211 : 19;
        pixels[offset + 1] = row * 7;
        pixels[offset + 2] = column < header.width / 2 ? 31 : 239;
        pixels[offset + 3] = 255;
    }
    uint32_t checksum = 2166136261u;
    for (unsigned char value : pixels) checksum = (checksum ^ value) * 16777619u;
    XrFrameSender sender;
    int64_t deadline = xr_frame_now() + 10000000000LL;
    uint64_t sent = 0;
    for (uint64_t id = 1; id <= 144; id++) {
        header.frame_id = id; header.captured_ns = xr_frame_now();
        bool delivered = sender.Send(argv[1], header, pixels.data());
        while (!delivered && sent == 0 && xr_frame_now() < deadline) {
            usleep(20000); header.captured_ns = xr_frame_now();
            delivered = sender.Send(argv[1], header, pixels.data());
        }
        if (!delivered) { std::fprintf(stderr, "frame %llu was not acknowledged\n", (unsigned long long)id); return 1; }
        sent++; usleep(13889);
    }
    std::printf("{\"sent\":%llu,\"lastFrameId\":%llu,\"checksum\":%u}\n",
        (unsigned long long)sent, (unsigned long long)header.frame_id, checksum);
}
