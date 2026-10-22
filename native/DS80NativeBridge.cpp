#include <xv-sdk.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <string>

#define API extern "C" __declspec(dllexport)

// Fixed C ABI. No std::string/vector/shared_ptr crosses the managed boundary.
struct NativePose {
    std::uint32_t version = 1, size = sizeof(NativePose);
    std::uint64_t sequence = 0;
    std::int64_t edgeTimestampUs = 0;
    double hostTimestamp = 0, confidence = 0;
    double x = 0, y = 0, z = 0;
    double qx = 0, qy = 0, qz = 0, qw = 1;
    double pitch = 0, yaw = 0, roll = 0; // SDK rotationToPitchYawRoll, radians
};
static_assert(sizeof(NativePose) == 120, "NativePose ABI changed");

namespace {
std::mutex lifecycle, frameGate;
std::map<std::string, std::shared_ptr<xv::Device>> devices;
std::shared_ptr<xv::Device> device;
std::shared_ptr<xv::Slam> slam;
int callbackId = -1;
NativePose latest;
std::string lastError;

bool copyString(const std::string& value, char* output, int capacity) {
    if (!output || capacity <= 0 || value.size() >= static_cast<std::size_t>(capacity)) return false;
    std::memcpy(output, value.c_str(), value.size() + 1);
    return true;
}

void copyPose(const xv::Pose& pose, NativePose& result) {
    // Same values and conversion function as the official all_stream sample.
    auto q = pose.quaternion();
    auto euler = xv::rotationToPitchYawRoll(pose.rotation());
    result.x = pose.x(); result.y = pose.y(); result.z = pose.z();
    result.qx = q[0]; result.qy = q[1]; result.qz = q[2]; result.qw = q[3];
    result.pitch = euler[0]; result.yaw = euler[1]; result.roll = euler[2];
    result.edgeTimestampUs = pose.edgeTimestampUs();
    result.hostTimestamp = pose.hostTimestamp();
    result.confidence = pose.confidence();
}

void closeDevice() {
    // Never hold frameGate while stopping the SDK: a callback may still be running.
    if (slam) {
        if (callbackId >= 0) slam->unregisterCallback(callbackId);
        callbackId = -1;
        slam->stop();
    }
    slam.reset(); device.reset(); devices.clear();
    std::lock_guard<std::mutex> frameLock(frameGate);
    latest = NativePose{};
}
}

API int ds80_abi_version() { return 1; }
API int ds80_pose_size() { return sizeof(NativePose); }

API int ds80_enumerate(char* serials, int capacity) {
    std::lock_guard<std::mutex> lock(lifecycle);
    try {
        if (device) { lastError = "Disconnect before refreshing devices."; return -1; }
        devices = xv::getDevicesUntilTimeout(3.0);
        int count = static_cast<int>(devices.size());
        std::string list;
        for (const auto& entry : devices) list += entry.first + "\n";
        // 枚举只返回序列号；立即释放句柄，避免刷新后长期占用设备（官方 Viewer 也要用）。
        devices.clear();
        if (!copyString(list, serials, capacity)) { lastError = "Serial buffer too small."; return -1; }
        lastError.clear();
        return count;
    } catch (const std::exception& e) { lastError = e.what(); devices.clear(); return -1; }
    catch (...) { lastError = "SDK enumeration failed."; devices.clear(); return -1; }
}

API int ds80_connect(const char* requestedSerial, char* actualSerial, int capacity) {
    std::lock_guard<std::mutex> lock(lifecycle);
    try {
        if (device) { lastError = "A device is already connected in this process."; return 0; }
        devices = xv::getDevicesUntilTimeout(3.0);
        if (devices.empty()) { lastError = "No Xvisio device found. Close SDK Viewer and check USB."; return 0; }
        std::string serial = requestedSerial ? requestedSerial : "";
        if (serial.empty()) {
            if (devices.size() != 1) { lastError = "Multiple devices found; select an explicit serial number."; return 0; }
            serial = devices.begin()->first;
        }
        auto found = devices.find(serial);
        if (found == devices.end()) { lastError = "Selected serial is not connected: " + serial; return 0; }
        if (!copyString(serial, actualSerial, capacity)) { lastError = "Serial buffer too small."; return 0; }
        auto selected = found->second;
        device = selected;
        slam = device->slam();
        // 只保留选中的设备，其余句柄立即释放。
        devices.clear();
        if (!slam) { lastError = "Selected device has no SLAM component."; closeDevice(); return 0; }
        { std::lock_guard<std::mutex> frameLock(frameGate); latest = NativePose{}; }
        callbackId = slam->registerCallback([](const xv::Pose& pose) {
            NativePose frame;
            copyPose(pose, frame);
            std::lock_guard<std::mutex> lock(frameGate);
            frame.sequence = latest.sequence + 1;
            latest = frame;
        });
        if (callbackId < 0 || !slam->start()) {
            lastError = "Could not start SDK SLAM callback."; closeDevice(); return 0;
        }
        lastError.clear();
        return 1;
    } catch (const std::exception& e) { lastError = e.what(); }
    catch (...) { lastError = "SDK connection failed."; }
    try { closeDevice(); } catch (...) {}
    return 0;
}

API int ds80_read_pose(NativePose* output, int capacity) {
    if (!output || capacity != sizeof(NativePose)) return -1;
    std::lock_guard<std::mutex> lock(frameGate);
    *output = latest;
    return latest.sequence ? 1 : 0;
}

API void ds80_disconnect() {
    std::lock_guard<std::mutex> lock(lifecycle);
    try { closeDevice(); } catch (...) { lastError = "SDK disconnect failed."; }
}

API void ds80_last_error(char* output, int capacity) {
    std::lock_guard<std::mutex> lock(lifecycle);
    copyString(lastError, output, capacity);
}

// Deterministic cross-language test of the exact callback conversion, without hardware.
API int ds80_test_pose(NativePose* output, int capacity) {
    if (!output || capacity != sizeof(NativePose)) return 0;
    // +90 degrees about Z. Translation stays in meters and is never negated.
    xv::Pose pose({1.25, -2.5, 3.75}, {0., -1., 0., 1., 0., 0., 0., 0., 1.}, 12.5, 123456, 0.9);
    *output = NativePose{};
    copyPose(pose, *output);
    output->sequence = 1;
    return 1;
}
