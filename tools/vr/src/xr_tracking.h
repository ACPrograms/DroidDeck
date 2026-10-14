#pragma once
#include <stdint.h>

#define DROIDDECK_XR_MAGIC 0x52584444u
#define DROIDDECK_XR_VERSION 1u
#define DROIDDECK_XR_FILE "droiddeck-xr-tracking"

struct DroidDeckXrPose {
    float orientation[4];
    float position[3];
    uint32_t flags;
};

struct DroidDeckXrEye {
    DroidDeckXrPose pose;
    float fov[4];
    uint32_t width, height;
};

struct DroidDeckXrHand {
    DroidDeckXrPose pose;
    float trigger, squeeze, stick[2];
    uint32_t buttons, active;
};

struct DroidDeckXrTracking {
    uint32_t magic, version, seq, active;
    int64_t sampled_ns, predicted_ns, period_ns;
    DroidDeckXrPose head;
    DroidDeckXrEye eyes[2];
    DroidDeckXrHand hands[2];
};

static_assert(sizeof(DroidDeckXrTracking) == 296, "XR tracking IPC layout");
