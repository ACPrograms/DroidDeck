#include <jni.h>
#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <android/log.h>
#include <android/native_window_jni.h>
#include <android_native_app_glue.h>
#include <time.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <array>
#include <vector>
#include <string>
#include <stdexcept>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <chrono>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <cerrno>
#include <cstddef>
#include <ctime>
#include "../../../../tools/vr/src/xr_tracking.h"
#include "../../../../tools/vr/src/xr_frame_channel.h"
#include "../../../../tools/vr/src/xr_frame_math.h"

static void check(XrResult result, const char *operation) {
    if (XR_FAILED(result)) throw std::runtime_error(std::string(operation) + ": " + std::to_string(result));
}

using Matrix = std::array<float, 16>;

static Matrix multiply(const Matrix &left, const Matrix &right) {
    Matrix out{};
    for (int column = 0; column < 4; column++)
        for (int row = 0; row < 4; row++)
            for (int inner = 0; inner < 4; inner++)
                out[column * 4 + row] += left[inner * 4 + row] * right[column * 4 + inner];
    return out;
}

static Matrix transform(const XrPosef &pose) {
    const auto &rotation = pose.orientation;
    Matrix matrix{
        1 - 2 * (rotation.y * rotation.y + rotation.z * rotation.z),
        2 * (rotation.x * rotation.y + rotation.w * rotation.z),
        2 * (rotation.x * rotation.z - rotation.w * rotation.y), 0,
        2 * (rotation.x * rotation.y - rotation.w * rotation.z),
        1 - 2 * (rotation.x * rotation.x + rotation.z * rotation.z),
        2 * (rotation.y * rotation.z + rotation.w * rotation.x), 0,
        2 * (rotation.x * rotation.z + rotation.w * rotation.y),
        2 * (rotation.y * rotation.z - rotation.w * rotation.x),
        1 - 2 * (rotation.x * rotation.x + rotation.y * rotation.y), 0,
        pose.position.x, pose.position.y, pose.position.z, 1
    };
    return matrix;
}

static Matrix viewProjection(const XrView &view) {
    XrPosef inverse{};
    inverse.orientation = {-view.pose.orientation.x, -view.pose.orientation.y,
                           -view.pose.orientation.z, view.pose.orientation.w};
    Matrix camera = transform(inverse);
    for (int row = 0; row < 3; row++)
        camera[12 + row] = -(camera[row] * view.pose.position.x + camera[4 + row] * view.pose.position.y
                            + camera[8 + row] * view.pose.position.z);
    float left = std::tan(view.fov.angleLeft), right = std::tan(view.fov.angleRight);
    float down = std::tan(view.fov.angleDown), up = std::tan(view.fov.angleUp);
    const float nearPlane = 0.05f, farPlane = 50.f;
    Matrix projection{};
    projection[0] = 2 / (right - left);
    projection[5] = 2 / (up - down);
    projection[8] = (right + left) / (right - left);
    projection[9] = (up + down) / (up - down);
    projection[10] = -(farPlane + nearPlane) / (farPlane - nearPlane);
    projection[11] = -1;
    projection[14] = -2 * farPlane * nearPlane / (farPlane - nearPlane);
    return multiply(projection, camera);
}

struct Eye {
    XrSwapchain swapchain = XR_NULL_HANDLE;
    int width = 0, height = 0;
    std::vector<XrSwapchainImageOpenGLESKHR> images;
    GLuint depth = 0;
};

class QuestXR {
public:
    explicit QuestXR(android_app *app) : app_(app) {}
    ~QuestXR() { cleanup(); }

    void run() {
        initialize();
        while (!app_->destroyRequested && !exit_) {
            android_poll_source *source = nullptr;
            int events = 0;
            int pollTimeout = running_ ? 0 : 100;
            for (int eventCount = 0; eventCount < 32; eventCount++) {
                if (ALooper_pollOnce(pollTimeout, nullptr, &events, reinterpret_cast<void **>(&source)) < 0) break;
                if (source) source->process(app_, source);
                if (app_->destroyRequested) break;
                pollTimeout = 0;
            }
            receiveFrame();
            pollEvents();
            if (running_ && !app_->destroyRequested) frame();
        }
        if (exit_ && !app_->destroyRequested) ANativeActivity_finish(app_->activity);
    }

    void health(const char *error = "") {
        int steamvrState = -1;
        if ((guestSurface_ || guestFramesInput_) && statusCallback_) {
            steamvrState = env_->CallIntMethod(app_->activity->clazz, statusCallback_);
            if (env_->ExceptionCheck()) { env_->ExceptionClear(); steamvrState = -1; }
        }
        std::string path = std::string(app_->activity->externalDataPath) + "/xr-health.json";
        std::string temp = path + ".tmp";
        FILE *file = std::fopen(temp.c_str(), "w");
        if (!file) return;
        std::fprintf(file,
            "{\"frames\":%llu,\"rendered\":%llu,\"sessionState\":%d,\"headFlags\":%llu,"
            "\"headPosition\":[%.6f,%.6f,%.6f],\"controllersActive\":[%s,%s],"
            "\"periodNs\":%lld,\"surfaceSwapchain\":%s,\"source\":\"%s\",\"guestFrames\":%lld,\"steamvrState\":%d,"
            "\"receivedFrames\":%llu,\"rejectedFrames\":%llu,\"uploadedFrames\":%llu,\"sourceFrameId\":%llu,"
            "\"gpuVerifiedFrameId\":%llu,\"gpuChecksum\":%u,\"error\":\"%s\"}\n",
            (unsigned long long)frames_, (unsigned long long)rendered_, state_,
            (unsigned long long)head_.locationFlags,
            head_.pose.position.x, head_.pose.position.y, head_.pose.position.z,
            handActive_[0] ? "true" : "false", handActive_[1] ? "true" : "false",
            (long long)period_, surfaceSupported_ ? "true" : "false", frameInput_ ? "frames" : guestSurface_ ? "guest" : surfaceTest_ ? "surface" : "native",
            (long long)guestFrames_, steamvrState, (unsigned long long)receiver_.Received(), (unsigned long long)receiver_.Rejected(),
            (unsigned long long)uploadedFrames_, (unsigned long long)sourceHeader_.frame_id,
            (unsigned long long)verifiedFrame_, gpuChecksum_, error);
        std::fclose(file);
        std::rename(temp.c_str(), path.c_str());
    }

private:
    android_app *app_;
    JNIEnv *env_ = nullptr;
    XrInstance instance_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    XrSystemId system_ = XR_NULL_SYSTEM_ID;
    XrSpace world_ = XR_NULL_HANDLE, view_ = XR_NULL_HANDLE;
    XrActionSet actions_ = XR_NULL_HANDLE;
    XrAction grip_ = XR_NULL_HANDLE, trigger_ = XR_NULL_HANDLE, vibration_ = XR_NULL_HANDLE;
    XrAction squeeze_ = XR_NULL_HANDLE, stick_ = XR_NULL_HANDLE, stickClick_ = XR_NULL_HANDLE;
    XrAction primary_ = XR_NULL_HANDLE, secondary_ = XR_NULL_HANDLE, menu_ = XR_NULL_HANDLE;
    DroidDeckXrHand inputs_[2]{};
    PFN_xrConvertTimeToTimespecTimeKHR convertTime_ = nullptr;
    bool timespecSupported_ = false;
    XrPath hands_[2]{};
    XrSpace handSpaces_[2]{};
    XrSpaceLocation head_{XR_TYPE_SPACE_LOCATION};
    XrSpaceLocation handLocations_[2]{{XR_TYPE_SPACE_LOCATION}, {XR_TYPE_SPACE_LOCATION}};
    bool handActive_[2]{}, pressed_[2]{};
    bool running_ = false, exit_ = false, surfaceSupported_ = false, quitRequested_ = false;
    bool surfaceTest_ = false;
    bool guestSurface_ = false, guestAttached_ = false;
    bool frameInput_ = false, verifyPixels_ = false, guestFramesInput_ = false;
    XrFrameReceiver receiver_;
    DroidDeckXrFrameHeader sourceHeader_{};
    uint64_t uploadedFrames_ = 0, verifiedFrame_ = 0;
    uint32_t gpuChecksum_ = 0;
    GLuint sourceTexture_ = 0, sourceProgram_ = 0;
    jmethodID surfaceCallback_ = nullptr, frameCallback_ = nullptr;
    jmethodID activeCallback_ = nullptr;
    jmethodID statusCallback_ = nullptr;
    jobject androidJavaSurface_ = nullptr;
    DroidDeckXrTracking *tracking_ = nullptr;
    DroidDeckXrHaptics *haptics_ = nullptr;
    uint64_t lastPulse_[2]{};
    XrSwapchain androidSwapchain_ = XR_NULL_HANDLE;
    ANativeWindow *androidWindow_ = nullptr;
    EGLSurface androidSurface_ = EGL_NO_SURFACE;
    XrSessionState state_ = XR_SESSION_STATE_UNKNOWN;
    uint64_t frames_ = 0, rendered_ = 0;
    int64_t guestFrames_ = 0;
    XrDuration period_ = 0;
    EGLDisplay display_ = EGL_NO_DISPLAY;
    EGLContext context_ = EGL_NO_CONTEXT;
    EGLSurface surface_ = EGL_NO_SURFACE;
    std::array<Eye, 2> eyes_;
    GLuint framebuffer_ = 0, program_ = 0, buffer_ = 0, vertexArray_ = 0;
    GLint matrixUniform_ = -1, colorUniform_ = -1;

    XrPath path(const char *text) {
        XrPath result{};
        check(xrStringToPath(instance_, text, &result), "xrStringToPath");
        return result;
    }

    void initialize() {
        app_->userData = this;
        app_->onInputEvent = [](android_app *app, AInputEvent *event) -> int32_t {
            if (AInputEvent_getType(event) != AINPUT_EVENT_TYPE_KEY || AKeyEvent_getKeyCode(event) != AKEYCODE_BACK
                || AKeyEvent_getAction(event) != AKEY_EVENT_ACTION_UP) return 0;
            auto *runtime = static_cast<QuestXR *>(app->userData);
            if (runtime->running_) {
                XrResult result = xrRequestExitSession(runtime->session_);
                if (XR_SUCCEEDED(result)) runtime->quitRequested_ = true;
                else __android_log_print(ANDROID_LOG_ERROR, "DroidDeckXR", "exit request failed: %d", result);
            } else ANativeActivity_finish(app->activity);
            return 1;
        };
        app_->activity->vm->AttachCurrentThread(&env_, nullptr);
        jclass activityClass = env_->GetObjectClass(app_->activity->clazz);
        jmethodID getIntent = env_->GetMethodID(activityClass, "getIntent", "()Landroid/content/Intent;");
        jobject intent = env_->CallObjectMethod(app_->activity->clazz, getIntent);
        jclass intentClass = env_->GetObjectClass(intent);
        jmethodID getBoolean = env_->GetMethodID(intentClass, "getBooleanExtra", "(Ljava/lang/String;Z)Z");
        jstring extra = env_->NewStringUTF("surface_test");
        surfaceTest_ = env_->CallBooleanMethod(intent, getBoolean, extra, JNI_FALSE);
        env_->DeleteLocalRef(extra);
        extra = env_->NewStringUTF("frame_input");
        frameInput_ = env_->CallBooleanMethod(intent, getBoolean, extra, JNI_FALSE);
        env_->DeleteLocalRef(extra);
        extra = env_->NewStringUTF("frame_verify");
        verifyPixels_ = env_->CallBooleanMethod(intent, getBoolean, extra, JNI_FALSE);
        env_->DeleteLocalRef(extra);
        extra = env_->NewStringUTF("guest_surface");
        guestSurface_ = env_->CallBooleanMethod(intent, getBoolean, extra, JNI_FALSE);
        env_->DeleteLocalRef(extra);
        extra = env_->NewStringUTF("guest_frames");
        guestFramesInput_ = env_->CallBooleanMethod(intent, getBoolean, extra, JNI_FALSE);
        if (guestFramesInput_) frameInput_ = true;
        if (guestSurface_ || guestFramesInput_) {
            if (guestSurface_) {
                surfaceTest_ = true;
                surfaceCallback_ = env_->GetMethodID(activityClass, "onXrSurface", "(Landroid/view/Surface;II)V");
            } else activeCallback_ = env_->GetMethodID(activityClass, "onXrActive", "(Z)V");
            frameCallback_ = env_->GetMethodID(activityClass, "onXrFrame", "(J)J");
            statusCallback_ = env_->GetMethodID(activityClass, "onXrStatus", "()I");
            if (env_->ExceptionCheck()) { env_->ExceptionClear(); throw std::runtime_error("XR activity bridge methods unavailable"); }
        }
        if (frameInput_ && (surfaceTest_ || guestSurface_)) throw std::runtime_error("Frame input cannot use an asynchronous surface projection");
        env_->DeleteLocalRef(extra); env_->DeleteLocalRef(intentClass);
        env_->DeleteLocalRef(intent); env_->DeleteLocalRef(activityClass);
        PFN_xrInitializeLoaderKHR initializeLoader = nullptr;
        check(xrGetInstanceProcAddr(XR_NULL_HANDLE, "xrInitializeLoaderKHR",
            reinterpret_cast<PFN_xrVoidFunction *>(&initializeLoader)), "loader init entrypoint");
        XrLoaderInitInfoAndroidKHR loader{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
        loader.applicationVM = app_->activity->vm;
        loader.applicationContext = app_->activity->clazz;
        check(initializeLoader(reinterpret_cast<XrLoaderInitInfoBaseHeaderKHR *>(&loader)), "xrInitializeLoaderKHR");
        uint32_t count = 0;
        check(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr), "extensions count");
        std::vector<XrExtensionProperties> extensions(count, {XR_TYPE_EXTENSION_PROPERTIES});
        check(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, extensions.data()), "extensions");
        for (const auto &extension : extensions) {
            if (!std::strcmp(extension.extensionName, XR_KHR_ANDROID_SURFACE_SWAPCHAIN_EXTENSION_NAME))
                surfaceSupported_ = true;
            if (!std::strcmp(extension.extensionName, XR_KHR_CONVERT_TIMESPEC_TIME_EXTENSION_NAME))
                timespecSupported_ = true;
        }
        if (surfaceTest_ && !surfaceSupported_) throw std::runtime_error("Android surface swapchains unavailable");
        std::vector<const char *> enabled{XR_KHR_ANDROID_CREATE_INSTANCE_EXTENSION_NAME, XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME};
        if (surfaceSupported_) enabled.push_back(XR_KHR_ANDROID_SURFACE_SWAPCHAIN_EXTENSION_NAME);
        if (timespecSupported_) enabled.push_back(XR_KHR_CONVERT_TIMESPEC_TIME_EXTENSION_NAME);
        XrInstanceCreateInfoAndroidKHR androidInfo{XR_TYPE_INSTANCE_CREATE_INFO_ANDROID_KHR};
        androidInfo.applicationVM = app_->activity->vm;
        androidInfo.applicationActivity = app_->activity->clazz;
        XrInstanceCreateInfo create{XR_TYPE_INSTANCE_CREATE_INFO};
        create.next = &androidInfo;
        std::strcpy(create.applicationInfo.applicationName, "DroidDeck XR");
        create.applicationInfo.applicationVersion = 1;
        create.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
        create.enabledExtensionCount = enabled.size();
        create.enabledExtensionNames = enabled.data();
        check(xrCreateInstance(&create, &instance_), "xrCreateInstance");
        if (timespecSupported_) check(xrGetInstanceProcAddr(instance_, "xrConvertTimeToTimespecTimeKHR",
            reinterpret_cast<PFN_xrVoidFunction *>(&convertTime_)), "time conversion entrypoint");
        XrSystemGetInfo systemInfo{XR_TYPE_SYSTEM_GET_INFO};
        systemInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        check(xrGetSystem(instance_, &systemInfo, &system_), "xrGetSystem");
        PFN_xrGetOpenGLESGraphicsRequirementsKHR requirementsFunction = nullptr;
        check(xrGetInstanceProcAddr(instance_, "xrGetOpenGLESGraphicsRequirementsKHR",
            reinterpret_cast<PFN_xrVoidFunction *>(&requirementsFunction)), "GLES requirements entrypoint");
        XrGraphicsRequirementsOpenGLESKHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
        check(requirementsFunction(instance_, system_, &requirements), "GLES requirements");
        if (requirements.minApiVersionSupported > XR_MAKE_VERSION(3, 0, 0)
            || requirements.maxApiVersionSupported < XR_MAKE_VERSION(3, 0, 0))
            throw std::runtime_error("Runtime does not support GLES 3.0");
        display_ = eglGetDisplay(EGL_DEFAULT_DISPLAY);
        if (!eglInitialize(display_, nullptr, nullptr)) throw std::runtime_error("eglInitialize failed");
        const EGLint attributes[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT | EGL_WINDOW_BIT,
            EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_DEPTH_SIZE, 24, EGL_NONE};
        EGLConfig config{};
        EGLint found = 0;
        if (!eglChooseConfig(display_, attributes, &config, 1, &found) || found != 1)
            throw std::runtime_error("EGL config unavailable");
        const EGLint contextAttributes[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
        context_ = eglCreateContext(display_, config, EGL_NO_CONTEXT, contextAttributes);
        const EGLint surfaceAttributes[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
        surface_ = eglCreatePbufferSurface(display_, config, surfaceAttributes);
        if (!eglMakeCurrent(display_, surface_, surface_, context_)) throw std::runtime_error("eglMakeCurrent failed");
        XrGraphicsBindingOpenGLESAndroidKHR binding{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
        binding.display = display_; binding.config = config; binding.context = context_;
        XrSessionCreateInfo sessionInfo{XR_TYPE_SESSION_CREATE_INFO};
        sessionInfo.next = &binding; sessionInfo.systemId = system_;
        check(xrCreateSession(instance_, &sessionInfo, &session_), "xrCreateSession");
        XrReferenceSpaceCreateInfo reference{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
        reference.poseInReferenceSpace.orientation.w = 1;
        reference.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
        XrResult referenceResult = xrCreateReferenceSpace(session_, &reference, &world_);
        if (referenceResult == XR_ERROR_REFERENCE_SPACE_UNSUPPORTED) {
            reference.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
            reference.poseInReferenceSpace.position.y = -1.6f;
            check(xrCreateReferenceSpace(session_, &reference, &world_), "local standing space");
        } else check(referenceResult, "stage space");
        reference.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
        reference.poseInReferenceSpace.position.y = 0;
        check(xrCreateReferenceSpace(session_, &reference, &view_), "view space");
        initializeActions();
        initializeImages();
        if (surfaceTest_) initializeAndroidSurface(config);
        initializeGraphics();
        initializeTracking();
        if (frameInput_) {
            std::string filename = std::string(app_->activity->internalDataPath) + "/.wayland-rt/" DROIDDECK_XR_FRAME_SOCKET;
            if (!receiver_.Start(filename)) throw std::runtime_error("XR frame receiver unavailable");
        }
        __android_log_print(ANDROID_LOG_INFO, "DroidDeckXR", "initialized GLES; Android surface swapchain supported=%d", surfaceSupported_);
        health();
    }

    void initializeActions() {
        hands_[0] = path("/user/hand/left"); hands_[1] = path("/user/hand/right");
        XrActionSetCreateInfo setInfo{XR_TYPE_ACTION_SET_CREATE_INFO};
        std::strcpy(setInfo.actionSetName, "quest"); std::strcpy(setInfo.localizedActionSetName, "Quest controllers");
        check(xrCreateActionSet(instance_, &setInfo, &actions_), "action set");
        auto action = [&](const char *name, XrActionType type, XrAction *handle) {
            XrActionCreateInfo info{XR_TYPE_ACTION_CREATE_INFO};
            std::strcpy(info.actionName, name); std::strcpy(info.localizedActionName, name);
            info.actionType = type; info.countSubactionPaths = 2; info.subactionPaths = hands_;
            check(xrCreateAction(actions_, &info, handle), "create action");
        };
        action("grip_pose", XR_ACTION_TYPE_POSE_INPUT, &grip_);
        action("trigger", XR_ACTION_TYPE_FLOAT_INPUT, &trigger_);
        action("squeeze", XR_ACTION_TYPE_FLOAT_INPUT, &squeeze_);
        action("stick", XR_ACTION_TYPE_VECTOR2F_INPUT, &stick_);
        action("stick_click", XR_ACTION_TYPE_BOOLEAN_INPUT, &stickClick_);
        action("primary", XR_ACTION_TYPE_BOOLEAN_INPUT, &primary_);
        action("secondary", XR_ACTION_TYPE_BOOLEAN_INPUT, &secondary_);
        action("menu", XR_ACTION_TYPE_BOOLEAN_INPUT, &menu_);
        action("vibration", XR_ACTION_TYPE_VIBRATION_OUTPUT, &vibration_);
        std::vector<XrActionSuggestedBinding> bindings;
        for (const char *hand : {"left", "right"}) {
            std::string root = std::string("/user/hand/") + hand;
            bindings.push_back({grip_, path((root + "/input/grip/pose").c_str())});
            bindings.push_back({trigger_, path((root + "/input/trigger/value").c_str())});
            bindings.push_back({squeeze_, path((root + "/input/squeeze/value").c_str())});
            bindings.push_back({stick_, path((root + "/input/thumbstick").c_str())});
            bindings.push_back({stickClick_, path((root + "/input/thumbstick/click").c_str())});
            bindings.push_back({primary_, path((root + (std::strcmp(hand, "left") == 0 ? "/input/x/click" : "/input/a/click")).c_str())});
            bindings.push_back({secondary_, path((root + (std::strcmp(hand, "left") == 0 ? "/input/y/click" : "/input/b/click")).c_str())});
            bindings.push_back({vibration_, path((root + "/output/haptic").c_str())});
        }
        bindings.push_back({menu_, path("/user/hand/left/input/menu/click")});
        XrInteractionProfileSuggestedBinding suggestion{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        suggestion.interactionProfile = path("/interaction_profiles/oculus/touch_controller");
        suggestion.countSuggestedBindings = bindings.size(); suggestion.suggestedBindings = bindings.data();
        check(xrSuggestInteractionProfileBindings(instance_, &suggestion), "Touch bindings");
        XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
        attach.countActionSets = 1; attach.actionSets = &actions_;
        check(xrAttachSessionActionSets(session_, &attach), "attach actions");
        for (int hand = 0; hand < 2; hand++) {
            XrActionSpaceCreateInfo space{XR_TYPE_ACTION_SPACE_CREATE_INFO};
            space.action = grip_; space.subactionPath = hands_[hand]; space.poseInActionSpace.orientation.w = 1;
            check(xrCreateActionSpace(session_, &space, &handSpaces_[hand]), "controller space");
        }
    }

    void initializeImages() {
        uint32_t count = 0;
        check(xrEnumerateViewConfigurationViews(instance_, system_, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
            0, &count, nullptr), "view count");
        if (count != 2) throw std::runtime_error("Expected two stereo views");
        std::vector<XrViewConfigurationView> views(count, {XR_TYPE_VIEW_CONFIGURATION_VIEW});
        check(xrEnumerateViewConfigurationViews(instance_, system_, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
            count, &count, views.data()), "view configuration");
        check(xrEnumerateSwapchainFormats(session_, 0, &count, nullptr), "format count");
        std::vector<int64_t> formats(count);
        check(xrEnumerateSwapchainFormats(session_, count, &count, formats.data()), "formats");
        int64_t format = 0;
        for (int64_t candidate : formats) if (candidate == GL_SRGB8_ALPHA8) format = candidate;
        if (!format) throw std::runtime_error("sRGB swapchain unavailable");
        for (int eye = 0; eye < 2; eye++) {
            Eye &target = eyes_[eye];
            target.width = views[eye].recommendedImageRectWidth;
            target.height = views[eye].recommendedImageRectHeight;
            if (surfaceTest_) continue;
            XrSwapchainCreateInfo create{XR_TYPE_SWAPCHAIN_CREATE_INFO};
            create.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT;
            create.format = format; create.sampleCount = 1;
            create.width = target.width; create.height = target.height;
            create.faceCount = 1; create.arraySize = 1; create.mipCount = 1;
            check(xrCreateSwapchain(session_, &create, &target.swapchain), "swapchain");
            check(xrEnumerateSwapchainImages(target.swapchain, 0, &count, nullptr), "image count");
            target.images.resize(count, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
            check(xrEnumerateSwapchainImages(target.swapchain, count, &count,
                reinterpret_cast<XrSwapchainImageBaseHeader *>(target.images.data())), "images");
            glGenRenderbuffers(1, &target.depth);
            glBindRenderbuffer(GL_RENDERBUFFER, target.depth);
            glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, target.width, target.height);
        }
    }

    void initializeAndroidSurface(EGLConfig config) {
        PFN_xrCreateSwapchainAndroidSurfaceKHR createSurface = nullptr;
        check(xrGetInstanceProcAddr(instance_, "xrCreateSwapchainAndroidSurfaceKHR",
            reinterpret_cast<PFN_xrVoidFunction *>(&createSurface)), "surface swapchain entrypoint");
        if (eyes_[0].width != eyes_[1].width || eyes_[0].height != eyes_[1].height)
            throw std::runtime_error("Surface path requires matching eye dimensions");
        XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT;
        info.width = eyes_[0].width * 2; info.height = eyes_[0].height;
        jobject surface = nullptr;
        check(createSurface(session_, &info, &androidSwapchain_, &surface), "Android surface swapchain");
        androidWindow_ = ANativeWindow_fromSurface(env_, surface);
        androidJavaSurface_ = env_->NewGlobalRef(surface);
        if (env_->GetObjectRefType(surface) == JNILocalRefType) env_->DeleteLocalRef(surface);
        if (!androidWindow_) throw std::runtime_error("Android swapchain has no native window");
        if (!guestSurface_) {
            androidSurface_ = eglCreateWindowSurface(display_, config, androidWindow_, nullptr);
            if (androidSurface_ == EGL_NO_SURFACE) throw std::runtime_error("Android EGL window surface failed");
        }
        __android_log_print(ANDROID_LOG_INFO, "DroidDeckXR", "Android projection surface %ux%u", info.width, info.height);
    }

    void initializeTracking() {
        std::string directory = std::string(app_->activity->internalDataPath) + "/.wayland-rt";
        if (mkdir(directory.c_str(), 0700) != 0 && errno != EEXIST)
            throw std::runtime_error("XR tracking directory unavailable");
        auto mapFile = [&](const char *name, size_t size) {
            std::string filename = directory + "/" + name;
            int descriptor = open(filename.c_str(), O_CREAT | O_RDWR | O_CLOEXEC, 0600);
            if (descriptor < 0) throw std::runtime_error("XR IPC file unavailable");
            if (ftruncate(descriptor, size) != 0) {
                close(descriptor); throw std::runtime_error("XR IPC file size failed");
            }
            void *mapping = mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_SHARED, descriptor, 0);
            close(descriptor);
            if (mapping == MAP_FAILED) throw std::runtime_error("XR IPC map failed");
            return mapping;
        };
        tracking_ = static_cast<DroidDeckXrTracking *>(mapFile(DROIDDECK_XR_FILE, sizeof(DroidDeckXrTracking)));
        haptics_ = static_cast<DroidDeckXrHaptics *>(mapFile(DROIDDECK_XR_HAPTIC_FILE, sizeof(DroidDeckXrHaptics)));
        haptics_->magic = DROIDDECK_XR_HAPTIC_MAGIC; haptics_->version = DROIDDECK_XR_VERSION;
        for (int hand = 0; hand < 2; hand++) lastPulse_[hand] = haptics_->hands[hand].id;
    }

    static DroidDeckXrPose posePacket(const XrPosef &pose, XrSpaceLocationFlags flags) {
        return {{pose.orientation.x, pose.orientation.y, pose.orientation.z, pose.orientation.w},
                {pose.position.x, pose.position.y, pose.position.z}, (uint32_t)flags};
    }

    void publishTracking(const XrView views[2], XrTime predicted) {
        if (!tracking_) return;
        timespec now{}; clock_gettime(CLOCK_MONOTONIC, &now);
        DroidDeckXrTracking packet{};
        packet.magic = DROIDDECK_XR_MAGIC; packet.version = DROIDDECK_XR_VERSION;
        packet.active = state_ == XR_SESSION_STATE_VISIBLE || state_ == XR_SESSION_STATE_FOCUSED;
        packet.sampled_ns = (int64_t)now.tv_sec * 1000000000LL + now.tv_nsec;
        if (convertTime_) {
            timespec displayTime{};
            check(convertTime_(instance_, predicted, &displayTime), "display time conversion");
            packet.predicted_ns = (int64_t)displayTime.tv_sec * 1000000000LL + displayTime.tv_nsec;
        }
        packet.period_ns = period_;
        packet.head = posePacket(head_.pose, head_.locationFlags);
        for (int eye = 0; eye < 2; eye++) {
            packet.eyes[eye].pose = posePacket(views[eye].pose, head_.locationFlags);
            packet.eyes[eye].fov[0] = views[eye].fov.angleLeft;
            packet.eyes[eye].fov[1] = views[eye].fov.angleRight;
            packet.eyes[eye].fov[2] = views[eye].fov.angleUp;
            packet.eyes[eye].fov[3] = views[eye].fov.angleDown;
            packet.eyes[eye].width = eyes_[eye].width; packet.eyes[eye].height = eyes_[eye].height;
            packet.hands[eye] = inputs_[eye];
            packet.hands[eye].pose = posePacket(handLocations_[eye].pose, handLocations_[eye].locationFlags);
            packet.hands[eye].active = handActive_[eye];
        }
        uint32_t sequence = __atomic_load_n(&tracking_->seq, __ATOMIC_RELAXED);
        __atomic_store_n(&tracking_->seq, sequence | 1u, __ATOMIC_SEQ_CST);
        __atomic_thread_fence(__ATOMIC_SEQ_CST);
        std::memcpy(tracking_, &packet, offsetof(DroidDeckXrTracking, seq));
        std::memcpy(reinterpret_cast<char *>(tracking_) + offsetof(DroidDeckXrTracking, active),
            reinterpret_cast<const char *>(&packet) + offsetof(DroidDeckXrTracking, active),
            sizeof(packet) - offsetof(DroidDeckXrTracking, active));
        __atomic_store_n(&tracking_->seq, (sequence | 1u) + 1u, __ATOMIC_RELEASE);
    }

    void attachGuest(bool attach) {
        if ((!guestSurface_ && !guestFramesInput_) || attach == guestAttached_) return;
        if (guestFramesInput_) env_->CallVoidMethod(app_->activity->clazz, activeCallback_, static_cast<jboolean>(attach));
        else env_->CallVoidMethod(app_->activity->clazz, surfaceCallback_, attach ? androidJavaSurface_ : nullptr,
                                 eyes_[0].width * 2, eyes_[0].height);
        if (env_->ExceptionCheck()) {
            env_->ExceptionDescribe(); env_->ExceptionClear(); throw std::runtime_error("Guest surface handshake failed");
        }
        guestAttached_ = attach;
    }

    void pollHaptics() {
        if (!haptics_) return;
        DroidDeckXrHaptics packet{};
        uint32_t sequence = __atomic_load_n(&haptics_->seq, __ATOMIC_ACQUIRE);
        if (sequence & 1u) return;
        std::memcpy(&packet, haptics_, sizeof(packet));
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (__atomic_load_n(&haptics_->seq, __ATOMIC_RELAXED) != sequence
            || packet.magic != DROIDDECK_XR_HAPTIC_MAGIC || packet.version != DROIDDECK_XR_VERSION) return;
        for (int hand = 0; hand < 2; hand++) {
            const auto &pulse = packet.hands[hand];
            if (pulse.id == lastPulse_[hand]) continue;
            lastPulse_[hand] = pulse.id;
            if (state_ != XR_SESSION_STATE_FOCUSED || !handActive_[hand]
                || pulse.duration_ns < 1000000 || pulse.duration_ns > 1000000000
                || !std::isfinite(pulse.amplitude) || pulse.amplitude < 0 || pulse.amplitude > 1
                || !std::isfinite(pulse.frequency) || pulse.frequency < 0) continue;
            XrHapticActionInfo info{XR_TYPE_HAPTIC_ACTION_INFO}; info.action = vibration_; info.subactionPath = hands_[hand];
            XrHapticVibration vibration{XR_TYPE_HAPTIC_VIBRATION};
            vibration.duration = pulse.duration_ns; vibration.amplitude = pulse.amplitude; vibration.frequency = pulse.frequency;
            check(xrApplyHapticFeedback(session_, &info, reinterpret_cast<XrHapticBaseHeader *>(&vibration)), "guest haptics");
        }
    }

    GLuint shader(GLenum type, const char *text) {
        GLuint handle = glCreateShader(type);
        glShaderSource(handle, 1, &text, nullptr); glCompileShader(handle);
        GLint success = 0; glGetShaderiv(handle, GL_COMPILE_STATUS, &success);
        if (!success) { glDeleteShader(handle); throw std::runtime_error("Shader compile failed"); }
        return handle;
    }

    void initializeGraphics() {
        GLuint vertex = shader(GL_VERTEX_SHADER,
            "#version 300 es\nlayout(location=0) in vec3 position; uniform mat4 matrix; void main(){gl_Position=matrix*vec4(position,1.0);}");
        GLuint fragment = shader(GL_FRAGMENT_SHADER,
            "#version 300 es\nprecision mediump float; uniform vec3 color; out vec4 outputColor; void main(){outputColor=vec4(color,1.0);}");
        program_ = glCreateProgram(); glAttachShader(program_, vertex); glAttachShader(program_, fragment);
        glLinkProgram(program_); glDeleteShader(vertex); glDeleteShader(fragment);
        GLint linked = 0; glGetProgramiv(program_, GL_LINK_STATUS, &linked);
        if (!linked) throw std::runtime_error("Shader link failed");
        matrixUniform_ = glGetUniformLocation(program_, "matrix"); colorUniform_ = glGetUniformLocation(program_, "color");
        const float vertices[] = {
            -1,-1,-1, 1,-1,-1, 1,1,-1, -1,-1,-1, 1,1,-1, -1,1,-1,
            -1,-1,1, 1,1,1, 1,-1,1, -1,-1,1, -1,1,1, 1,1,1,
            -1,-1,-1, -1,1,-1, -1,1,1, -1,-1,-1, -1,1,1, -1,-1,1,
            1,-1,-1, 1,-1,1, 1,1,1, 1,-1,-1, 1,1,1, 1,1,-1,
            -1,-1,-1, -1,-1,1, 1,-1,1, -1,-1,-1, 1,-1,1, 1,-1,-1,
            -1,1,-1, 1,1,-1, 1,1,1, -1,1,-1, 1,1,1, -1,1,1};
        glGenVertexArrays(1, &vertexArray_); glBindVertexArray(vertexArray_);
        glGenBuffers(1, &buffer_); glBindBuffer(GL_ARRAY_BUFFER, buffer_);
        glBufferData(GL_ARRAY_BUFFER, sizeof(vertices), vertices, GL_STATIC_DRAW);
        glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 0, nullptr); glEnableVertexAttribArray(0);
        glGenFramebuffers(1, &framebuffer_);
        if (frameInput_) {
            vertex = shader(GL_VERTEX_SHADER,
                "#version 300 es\nout vec2 uv; uniform float eye; void main(){vec2 p=vec2(float((gl_VertexID<<1)&2),float(gl_VertexID&2));uv=vec2(p.x*0.5+eye*0.5,1.0-p.y);gl_Position=vec4(p*2.0-1.0,0.0,1.0);}");
            fragment = shader(GL_FRAGMENT_SHADER,
                "#version 300 es\nprecision highp float; in vec2 uv; uniform sampler2D source; uniform float eye; out vec4 outputColor; void main(){float edge=0.5/float(textureSize(source,0).x);vec2 p=vec2(clamp(uv.x,eye*0.5+edge,(eye+1.0)*0.5-edge),uv.y);outputColor=texture(source,p);}");
            sourceProgram_ = glCreateProgram(); glAttachShader(sourceProgram_, vertex); glAttachShader(sourceProgram_, fragment);
            glLinkProgram(sourceProgram_); glDeleteShader(vertex); glDeleteShader(fragment);
            glGetProgramiv(sourceProgram_, GL_LINK_STATUS, &linked);
            if (!linked) throw std::runtime_error("Frame shader link failed");
            glGenTextures(1, &sourceTexture_); glBindTexture(GL_TEXTURE_2D, sourceTexture_);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        }
    }

    void receiveFrame() {
        if (!frameInput_) return;
        DroidDeckXrPixels frame;
        if (!receiver_.Take(&frame)) return;
        GLint maximum = 0; glGetIntegerv(GL_MAX_TEXTURE_SIZE, &maximum);
        if (frame.header.width > static_cast<uint32_t>(maximum) || frame.header.height > static_cast<uint32_t>(maximum))
            throw std::runtime_error("XR source exceeds GPU texture limit");
        glBindTexture(GL_TEXTURE_2D, sourceTexture_);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_SRGB8_ALPHA8, frame.header.width, frame.header.height, 0,
                     GL_RGBA, GL_UNSIGNED_BYTE, frame.pixels.data());
        if (verifyPixels_) {
            glBindFramebuffer(GL_FRAMEBUFFER, framebuffer_);
            glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, 0);
            glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, sourceTexture_, 0);
            if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
                throw std::runtime_error("Frame readback framebuffer incomplete");
            std::vector<unsigned char> readback(frame.header.bytes);
            glReadPixels(0, 0, frame.header.width, frame.header.height, GL_RGBA, GL_UNSIGNED_BYTE, readback.data());
            if (readback != frame.pixels) throw std::runtime_error("Frame GPU readback does not match owned pixels");
            gpuChecksum_ = 2166136261u;
            for (unsigned char value : readback) gpuChecksum_ = (gpuChecksum_ ^ value) * 16777619u;
            verifiedFrame_ = frame.header.frame_id;
            glBindFramebuffer(GL_FRAMEBUFFER, 0);
        }
        if (glGetError() != GL_NO_ERROR) throw std::runtime_error("Frame texture upload failed");
        sourceHeader_ = frame.header;
        uploadedFrames_++;
        if (verifyPixels_ || uploadedFrames_ % 72 == 1) health();
    }

    void cube(const Matrix &camera, const XrPosef &pose, float size, float red, float green, float blue) {
        Matrix model = transform(pose);
        for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++) model[column * 4 + row] *= size;
        Matrix matrix = multiply(camera, model);
        glUniformMatrix4fv(matrixUniform_, 1, GL_FALSE, matrix.data());
        glUniform3f(colorUniform_, red, green, blue); glDrawArrays(GL_TRIANGLES, 0, 36);
    }

    void render(const XrView &view, Eye &eye, uint32_t image) {
        glBindFramebuffer(GL_FRAMEBUFFER, framebuffer_);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, eye.images[image].image, 0);
        glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, eye.depth);
        if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
            throw std::runtime_error("Incomplete eye framebuffer");
        if (frameInput_) {
            glViewport(0, 0, eye.width, eye.height); glDisable(GL_DEPTH_TEST);
            glUseProgram(sourceProgram_); glBindVertexArray(vertexArray_);
            glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, sourceTexture_);
            glUniform1i(glGetUniformLocation(sourceProgram_, "source"), 0);
            glUniform1f(glGetUniformLocation(sourceProgram_, "eye"), &eye == &eyes_[1] ? 1.f : 0.f);
            glDrawArrays(GL_TRIANGLES, 0, 3);
        } else scene(view, eye.width, eye.height, 0);
        glBindFramebuffer(GL_FRAMEBUFFER, 0);
        if (glGetError() != GL_NO_ERROR) throw std::runtime_error("OpenGL render error");
        glFlush();
    }

    void scene(const XrView &view, int width, int height, int offset) {
        glViewport(offset, 0, width, height);
        glEnable(GL_SCISSOR_TEST); glScissor(offset, 0, width, height);
        glEnable(GL_DEPTH_TEST); glClearColor(0.015f, 0.025f, 0.06f, 1);
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        glUseProgram(program_); glBindVertexArray(vertexArray_);
        Matrix camera = viewProjection(view);
        XrPosef pose{}; pose.orientation.w = 1;
        for (int column = -4; column <= 4; column++) {
            pose.position = {column * 0.6f, 0.2f, -3};
            cube(camera, pose, 0.08f, 0.1f, 0.3f, 0.8f);
        }
        pose.position = {0, 1.4f, -2}; cube(camera, pose, 0.2f, 0.1f, 0.9f, 0.4f);
        for (int hand = 0; hand < 2; hand++)
            if (handActive_[hand] && (handLocations_[hand].locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT))
                cube(camera, handLocations_[hand].pose, 0.06f, hand == 0 ? 0.9f : 0.2f, 0.3f, hand == 1 ? 0.9f : 0.2f);
        glDisable(GL_SCISSOR_TEST);
    }

    void pollEvents() {
        XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
        XrResult result;
        while ((result = xrPollEvent(instance_, &event)) == XR_SUCCESS) {
            if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                state_ = reinterpret_cast<XrEventDataSessionStateChanged *>(&event)->state;
                __android_log_print(ANDROID_LOG_INFO, "DroidDeckXR", "session state=%d", state_);
                attachGuest(state_ == XR_SESSION_STATE_VISIBLE || state_ == XR_SESSION_STATE_FOCUSED);
                if (state_ == XR_SESSION_STATE_READY) {
                    XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO};
                    begin.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                    check(xrBeginSession(session_, &begin), "xrBeginSession"); running_ = true;
                } else if (state_ == XR_SESSION_STATE_STOPPING) {
                    check(xrEndSession(session_), "xrEndSession"); running_ = false;
                    if (quitRequested_) exit_ = true;
                } else if (state_ == XR_SESSION_STATE_EXITING || state_ == XR_SESSION_STATE_LOSS_PENDING) exit_ = true;
                health();
            } else if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) exit_ = true;
            event = {XR_TYPE_EVENT_DATA_BUFFER};
        }
        check(result, "xrPollEvent");
    }

    void frame() {
        XrFrameWaitInfo wait{XR_TYPE_FRAME_WAIT_INFO};
        XrFrameState frameState{XR_TYPE_FRAME_STATE};
        check(xrWaitFrame(session_, &wait, &frameState), "xrWaitFrame");
        period_ = frameState.predictedDisplayPeriod;
        XrFrameBeginInfo begin{XR_TYPE_FRAME_BEGIN_INFO}; check(xrBeginFrame(session_, &begin), "xrBeginFrame");
        check(xrLocateSpace(view_, world_, frameState.predictedDisplayTime, &head_), "head pose");
        if (state_ == XR_SESSION_STATE_FOCUSED) {
            XrActiveActionSet active{actions_, XR_NULL_PATH};
            XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO}; sync.countActiveActionSets = 1; sync.activeActionSets = &active;
            check(xrSyncActions(session_, &sync), "xrSyncActions");
            for (int hand = 0; hand < 2; hand++) {
                XrActionStateGetInfo get{XR_TYPE_ACTION_STATE_GET_INFO}; get.action = grip_; get.subactionPath = hands_[hand];
                XrActionStatePose pose{XR_TYPE_ACTION_STATE_POSE}; check(xrGetActionStatePose(session_, &get, &pose), "grip state");
                handActive_[hand] = pose.isActive;
                check(xrLocateSpace(handSpaces_[hand], world_, frameState.predictedDisplayTime, &handLocations_[hand]), "hand pose");
                get.action = trigger_;
                XrActionStateFloat trigger{XR_TYPE_ACTION_STATE_FLOAT}; check(xrGetActionStateFloat(session_, &get, &trigger), "trigger state");
                inputs_[hand].trigger = trigger.isActive ? trigger.currentState : 0;
                get.action = squeeze_;
                XrActionStateFloat squeeze{XR_TYPE_ACTION_STATE_FLOAT}; check(xrGetActionStateFloat(session_, &get, &squeeze), "squeeze state");
                inputs_[hand].squeeze = squeeze.isActive ? squeeze.currentState : 0;
                get.action = stick_;
                XrActionStateVector2f stick{XR_TYPE_ACTION_STATE_VECTOR2F}; check(xrGetActionStateVector2f(session_, &get, &stick), "stick state");
                inputs_[hand].stick[0] = stick.isActive ? stick.currentState.x : 0;
                inputs_[hand].stick[1] = stick.isActive ? stick.currentState.y : 0;
                inputs_[hand].buttons = 0;
                XrAction buttons[] = {stickClick_, primary_, secondary_, menu_};
                for (int button = 0; button < 4; button++) {
                    get.action = buttons[button];
                    XrActionStateBoolean value{XR_TYPE_ACTION_STATE_BOOLEAN}; check(xrGetActionStateBoolean(session_, &get, &value), "button state");
                    if (value.isActive && value.currentState) inputs_[hand].buttons |= 1u << button;
                }
                bool pressed = trigger.isActive && trigger.currentState > 0.8f;
                if (pressed && !pressed_[hand]) {
                    XrHapticActionInfo info{XR_TYPE_HAPTIC_ACTION_INFO}; info.action = vibration_; info.subactionPath = hands_[hand];
                    XrHapticVibration pulse{XR_TYPE_HAPTIC_VIBRATION}; pulse.duration = 30000000; pulse.frequency = XR_FREQUENCY_UNSPECIFIED; pulse.amplitude = 0.3f;
                    check(xrApplyHapticFeedback(session_, &info, reinterpret_cast<XrHapticBaseHeader *>(&pulse)), "haptic pulse");
                }
                pressed_[hand] = pressed;
            }
        } else handActive_[0] = handActive_[1] = false;
        pollHaptics();
        XrViewLocateInfo locate{XR_TYPE_VIEW_LOCATE_INFO}; locate.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        locate.displayTime = frameState.predictedDisplayTime; locate.space = world_;
        XrViewState viewState{XR_TYPE_VIEW_STATE}; uint32_t count = 0;
        XrView views[2]{{XR_TYPE_VIEW}, {XR_TYPE_VIEW}};
        check(xrLocateViews(session_, &locate, &viewState, 2, &count, views), "xrLocateViews");
        publishTracking(views, frameState.predictedDisplayTime);
        if ((guestSurface_ || guestFramesInput_) && guestAttached_) {
            timespec now{}; clock_gettime(CLOCK_MONOTONIC, &now);
            guestFrames_ = env_->CallLongMethod(app_->activity->clazz, frameCallback_, (jlong)now.tv_sec * 1000000000LL + now.tv_nsec);
            if (env_->ExceptionCheck()) { env_->ExceptionClear(); throw std::runtime_error("Guest frame callback failed"); }
        }
        XrCompositionLayerProjectionView projection[2]{{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
        bool valid = frameState.shouldRender && (!surfaceTest_ || state_ == XR_SESSION_STATE_VISIBLE || state_ == XR_SESSION_STATE_FOCUSED) && count == 2 &&
            (viewState.viewStateFlags & (XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT))
            == (XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT);
        if (frameInput_) {
            valid = valid && sourceHeader_.frame_id && xr_frame_now() - sourceHeader_.captured_ns < 500000000LL;
            if (valid) for (int eye = 0; eye < 2; eye++) {
                auto source = xr_frame_view(sourceHeader_, eye);
                views[eye].pose.orientation = {source.orientation[0], source.orientation[1], source.orientation[2], source.orientation[3]};
                views[eye].pose.position = {source.position[0], source.position[1], source.position[2]};
                views[eye].fov = {source.fov[0], source.fov[1], source.fov[2], source.fov[3]};
            }
        }
        if (valid && surfaceTest_) {
            if (!guestSurface_ && !eglMakeCurrent(display_, androidSurface_, androidSurface_, context_))
                throw std::runtime_error("Surface context binding failed");
            glBindFramebuffer(GL_FRAMEBUFFER, 0);
            for (int eye = 0; eye < 2; eye++) {
                if (!guestSurface_) scene(views[eye], eyes_[eye].width, eyes_[eye].height, eye * eyes_[eye].width);
                projection[eye].pose = views[eye].pose; projection[eye].fov = views[eye].fov;
                projection[eye].subImage.swapchain = androidSwapchain_;
                projection[eye].subImage.imageRect.offset = {eye * eyes_[eye].width, 0};
                projection[eye].subImage.imageRect.extent = {eyes_[eye].width, eyes_[eye].height};
            }
            if (glGetError() != GL_NO_ERROR) throw std::runtime_error("Surface rendering error");
            if (!guestSurface_ && !eglSwapBuffers(display_, androidSurface_)) throw std::runtime_error("Surface presentation failed");
        } else if (valid) for (int eye = 0; eye < 2; eye++) {
            uint32_t image = 0;
            XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
            check(xrAcquireSwapchainImage(eyes_[eye].swapchain, &acquire, &image), "acquire image");
            XrSwapchainImageWaitInfo imageWait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO}; imageWait.timeout = XR_INFINITE_DURATION;
            check(xrWaitSwapchainImage(eyes_[eye].swapchain, &imageWait), "wait image");
            render(views[eye], eyes_[eye], image);
            XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
            check(xrReleaseSwapchainImage(eyes_[eye].swapchain, &release), "release image");
            projection[eye].pose = views[eye].pose; projection[eye].fov = views[eye].fov;
            projection[eye].subImage.swapchain = eyes_[eye].swapchain;
            projection[eye].subImage.imageRect.extent = {eyes_[eye].width, eyes_[eye].height};
        }
        XrCompositionLayerProjection layer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
        layer.space = world_; layer.viewCount = 2; layer.views = projection;
        const XrCompositionLayerBaseHeader *layers[] = {reinterpret_cast<XrCompositionLayerBaseHeader *>(&layer)};
        XrFrameEndInfo end{XR_TYPE_FRAME_END_INFO}; end.displayTime = frameState.predictedDisplayTime;
        end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE; end.layerCount = valid ? 1 : 0; end.layers = layers;
        check(xrEndFrame(session_, &end), "xrEndFrame");
        frames_++; if (valid) rendered_++;
        if (frames_ % 72 == 0) {
            health();
            __android_log_print(ANDROID_LOG_INFO, "DroidDeckXR", "frames=%llu rendered=%llu head=%llu hands=%d,%d period=%lld",
                (unsigned long long)frames_, (unsigned long long)rendered_, (unsigned long long)head_.locationFlags,
                handActive_[0], handActive_[1], (long long)period_);
        }
    }

    void cleanup() {
        receiver_.Stop();
        try { attachGuest(false); } catch (const std::exception &error) {
            __android_log_print(ANDROID_LOG_ERROR, "DroidDeckXR", "cleanup: %s", error.what());
        }
        if (tracking_) {
            __atomic_store_n(&tracking_->active, 0u, __ATOMIC_RELEASE);
            munmap(tracking_, sizeof(DroidDeckXrTracking));
        }
        if (haptics_) munmap(haptics_, sizeof(DroidDeckXrHaptics));
        if (display_ != EGL_NO_DISPLAY && context_ != EGL_NO_CONTEXT) {
            glDeleteFramebuffers(1, &framebuffer_); glDeleteProgram(program_);
            glDeleteBuffers(1, &buffer_); glDeleteVertexArrays(1, &vertexArray_);
            glDeleteTextures(1, &sourceTexture_); glDeleteProgram(sourceProgram_);
            for (auto &eye : eyes_) glDeleteRenderbuffers(1, &eye.depth);
        }
        if (androidSurface_ != EGL_NO_SURFACE) {
            eglMakeCurrent(display_, surface_, surface_, context_);
            eglDestroySurface(display_, androidSurface_);
        }
        if (androidWindow_) ANativeWindow_release(androidWindow_);
        if (androidSwapchain_) xrDestroySwapchain(androidSwapchain_);
        if (androidJavaSurface_) env_->DeleteGlobalRef(androidJavaSurface_);
        for (auto &eye : eyes_) if (eye.swapchain) xrDestroySwapchain(eye.swapchain);
        for (auto space : handSpaces_) if (space) xrDestroySpace(space);
        if (view_) xrDestroySpace(view_); if (world_) xrDestroySpace(world_);
        if (session_) xrDestroySession(session_);
        if (actions_) xrDestroyActionSet(actions_);
        if (instance_) xrDestroyInstance(instance_);
        if (display_ != EGL_NO_DISPLAY) {
            eglMakeCurrent(display_, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
            if (surface_ != EGL_NO_SURFACE) eglDestroySurface(display_, surface_);
            if (context_ != EGL_NO_CONTEXT) eglDestroyContext(display_, context_);
            eglTerminate(display_);
        }
        if (env_) app_->activity->vm->DetachCurrentThread();
    }
};

void android_main(android_app *app) {
    QuestXR runtime(app);
    try { runtime.run(); }
    catch (const std::exception &error) {
        __android_log_print(ANDROID_LOG_ERROR, "DroidDeckXR", "%s", error.what());
        runtime.health(error.what());
        ANativeActivity_finish(app->activity);
    }
}
