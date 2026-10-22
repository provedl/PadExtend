// padextend_shared.h - 驱动与发送端共享内存帧契约（单一数据源，两端包含此文件）。
// 命名空间用 Local\（同一用户会话内 UMDF 驱动宿主进程与 sender 可互通）。
#ifndef PADEXTEND_SHARED_H
#define PADEXTEND_SHARED_H
#include <stdint.h>

#define PADEXTEND_SHM_NAME      L"Global\\PadExtendFrame"
#define PADEXTEND_EVENT_NAME    L"Global\\PadExtendFrameReady"
#define PADEXTEND_SHM_MAGIC     0x50455853u  // 'PEXS'
#define PADEXTEND_SHM_VERSION   1u

// 最大支持 4K（3840x2160 BGRA = ~33MB）。共享段 = 头 + 该上限像素区。
#define PADEXTEND_MAX_WIDTH     3840u
#define PADEXTEND_MAX_HEIGHT    2160u
#define PADEXTEND_MAX_PIXELS    ((uint64_t)PADEXTEND_MAX_WIDTH * PADEXTEND_MAX_HEIGHT * 4u)

#pragma pack(push, 4)
typedef struct PadExtendShmHeader {
    uint32_t magic;       // PADEXTEND_SHM_MAGIC
    uint32_t version;     // PADEXTEND_SHM_VERSION
    uint32_t width;       // 当前帧宽（像素）
    uint32_t height;      // 当前帧高（像素）
    uint32_t rowPitch;    // 每行字节数（可能有对齐 padding，> width*4）
    uint32_t format;      // DXGI_FORMAT，预期 87 (B8G8R8A8_UNORM)
    volatile uint32_t seqBefore; // 写入前自增；读端用它与 seqAfter 校验一致性
    volatile uint32_t seqAfter;  // 写入后自增；seqBefore==seqAfter 表示该帧完整
    uint64_t payloadBytes;       // 像素有效字节数 = rowPitch * height（去 padding 由读端按行处理）
    uint32_t reserved[6];
} PadExtendShmHeader;
#pragma pack(pop)

#define PADEXTEND_SHM_HEADER_SIZE ((uint32_t)sizeof(PadExtendShmHeader))
// 整个共享段大小：头 + 最大像素区（固定大小，避免重建映射）。
#define PADEXTEND_SHM_SIZE ((uint64_t)PADEXTEND_SHM_HEADER_SIZE + PADEXTEND_MAX_PIXELS)

#endif

