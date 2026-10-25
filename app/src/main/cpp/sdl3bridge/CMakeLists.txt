# sdl3droiddeck: SDL3 and the SDL3 bridge's controller loop, built into the app (libSDL3.so + libmain.so, loaded by
# the bridge in its own ":sdl3" process). Written by patch-droiddeck.sh (step 30); the sources are SdlBridge's.
set(SDL_SHARED ON CACHE BOOL "" FORCE)
set(SDL_STATIC OFF CACHE BOOL "" FORCE)
set(SDL_TEST_LIBRARY OFF CACHE BOOL "" FORCE)
set(SDL_TESTS OFF CACHE BOOL "" FORCE)
set(SDL_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SDL_ANDROID_JAR OFF CACHE BOOL "" FORCE)
set(SDL_INSTALL OFF CACHE BOOL "" FORCE)
# The bridge draws nothing (SDL3 runs headless for controllers): no OpenGL ES, so libSDL3.so needs no libGLESv1_CM.so,
# which DroidDeck's APK check (tools/release/check_android_libraries.py) does not count as a system library.
set(SDL_OPENGLES OFF CACHE BOOL "" FORCE)
include(FetchContent)
if(POLICY CMP0135)
    cmake_policy(SET CMP0135 NEW)
endif()
FetchContent_Declare(sdl3
    URL https://github.com/libsdl-org/SDL/releases/download/release-3.4.18/SDL3-3.4.18.tar.gz
    URL_HASH SHA256=9c75cf16330322c217dedd2e0609f1124f1b54b8633e763467b4684d0f4334a3
)
FetchContent_MakeAvailable(sdl3)
add_library(main SHARED src/main.c)
target_link_libraries(main PRIVATE SDL3::SDL3 log)
