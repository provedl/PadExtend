// padextend_encoder.h - C ABI for the PadExtend H.264 encoder (Media Foundation backed).
#ifndef PADEXTEND_ENCODER_H
#define PADEXTEND_ENCODER_H
#include <stdint.h>

#ifdef PADEXTEND_ENCODER_EXPORTS
#define PEX_API __declspec(dllexport)
#else
#define PEX_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void* PexEncoder;

// Create an encoder for the given dimensions/params. Returns NULL on failure.
// width/height must be even. bitrate in bits/sec. fps frames/sec.
// keyIntervalFrames: force an IDR every N frames (<=0 => encoder default).
PEX_API PexEncoder PexEncoderCreate(int width, int height, int fps, int bitrate, int keyIntervalFrames);

// Encode one top-down BGRA frame (stride = width*4, size = width*height*4).
// On success returns 0 and fills *outPtr/*outLen with an Annex-B buffer owned by
// the encoder (valid until the next Encode/Destroy). outLen may be 0 (no output yet).
// *isKeyframe set to 1 if the produced access unit contains an IDR.
PEX_API int PexEncoderEncode(PexEncoder enc, const uint8_t* bgra, int bgraLen,
                             const uint8_t** outPtr, int* outLen, int* isKeyframe);

// Retrieve codec private data (SPS+PPS, Annex-B). Valid after first keyframe.
// Returns 0 on success; fills *outPtr/*outLen (owned by encoder).
PEX_API int PexEncoderGetConfig(PexEncoder enc, const uint8_t** outPtr, int* outLen);

// Request that the next encoded frame be an IDR keyframe.
PEX_API void PexEncoderForceKeyframe(PexEncoder enc);

PEX_API void PexEncoderDestroy(PexEncoder enc);

#ifdef __cplusplus
}
#endif
#endif
