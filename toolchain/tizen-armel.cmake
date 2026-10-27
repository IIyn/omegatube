set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER arm-linux-gnueabi-gcc)
set(CMAKE_CXX_COMPILER arm-linux-gnueabi-g++)

# Tizen TV userland is armv7 with the softfp float ABI (not hard-float).
set(TIZEN_ARCH_FLAGS "-march=armv7-a -mfpu=neon -mfloat-abi=softfp -mthumb")
set(CMAKE_C_FLAGS_INIT "${TIZEN_ARCH_FLAGS}")
set(CMAKE_CXX_FLAGS_INIT "${TIZEN_ARCH_FLAGS}")

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
