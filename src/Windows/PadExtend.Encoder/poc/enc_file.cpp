// enc_file.cpp - MF H.264 encode POC
// Reads frame.bmp (32bpp BGRA), converts to NV12, encodes N frames with the
// Media Foundation H.264 encoder MFT, writes Annex-B stream to out.h264.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <cstdio>
#include <cstdint>
#include <vector>
#include <wrl/client.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "strmiids.lib")

using Microsoft::WRL::ComPtr;

#define CHECK(hr, msg) do { HRESULT _hr=(hr); if(FAILED(_hr)){ wprintf(L"FAIL %s 0x%08X\n", msg, _hr); return 1; } } while(0)

#pragma pack(push,1)
struct BmpFileHeader { uint16_t bfType; uint32_t bfSize; uint16_t r1,r2; uint32_t bfOffBits; };
struct BmpInfoHeader { uint32_t biSize; int32_t biWidth; int32_t biHeight; uint16_t biPlanes; uint16_t biBitCount;
    uint32_t biCompression; uint32_t biSizeImage; int32_t x,y; uint32_t clrUsed, clrImp; };
#pragma pack(pop)

// Load a 32bpp BGRA bottom-up BMP into top-down BGRA buffer.
static bool LoadBmpBgra(const wchar_t* path, std::vector<uint8_t>& bgra, int& w, int& h) {
    FILE* f = _wfopen(path, L"rb");
    if (!f) return false;
    BmpFileHeader fh; BmpInfoHeader ih;
    if (fread(&fh,sizeof(fh),1,f)!=1 || fread(&ih,sizeof(ih),1,f)!=1) { fclose(f); return false; }
    if (ih.biBitCount != 32) { fclose(f); wprintf(L"expected 32bpp bmp, got %u\n", ih.biBitCount); return false; }
    w = ih.biWidth; h = (ih.biHeight<0)? -ih.biHeight : ih.biHeight;
    bool bottomUp = ih.biHeight > 0;
    std::vector<uint8_t> raw((size_t)w*h*4);
    fseek(f, fh.bfOffBits, SEEK_SET);
    if (fread(raw.data(),1,raw.size(),f)!=raw.size()) { fclose(f); return false; }
    fclose(f);
    bgra.resize((size_t)w*h*4);
    for (int row=0; row<h; ++row) {
        int src = bottomUp ? (h-1-row) : row;
        memcpy(&bgra[(size_t)row*w*4], &raw[(size_t)src*w*4], (size_t)w*4);
    }
    return true;
}

// BT.601 full-range BGRA -> NV12.
static void BgraToNv12(const uint8_t* bgra, int w, int h, uint8_t* nv12) {
    uint8_t* y = nv12;
    uint8_t* uv = nv12 + (size_t)w*h;
    for (int j=0;j<h;++j) for (int i=0;i<w;++i) {
        const uint8_t* p = bgra + ((size_t)j*w+i)*4;
        int B=p[0],G=p[1],R=p[2];
        int Y = ( 66*R +129*G + 25*B +128)>>8; Y+=16;
        y[(size_t)j*w+i] = (uint8_t)(Y<0?0:Y>255?255:Y);
    }
    for (int j=0;j<h;j+=2) for (int i=0;i<w;i+=2) {
        const uint8_t* p = bgra + ((size_t)j*w+i)*4;
        int B=p[0],G=p[1],R=p[2];
        int U = (-38*R -74*G +112*B +128)>>8; U+=128;
        int V = (112*R -94*G -18*B +128)>>8; V+=128;
        uint8_t* d = uv + ((size_t)(j/2)*w) + (i); // (w/2 pairs)*2 = w stride
        d[0]=(uint8_t)(U<0?0:U>255?255:U);
        d[1]=(uint8_t)(V<0?0:V>255?255:V);
    }
}

int wmain(int argc, wchar_t** argv) {
    const wchar_t* inPath = (argc>1)? argv[1] : L"C:\\ProgramData\\PadExtend\\frame.bmp";
    const wchar_t* outPath= (argc>2)? argv[2] : L"out.h264";
    int frames = (argc>3)? _wtoi(argv[3]) : 30;
    int fps = 30, bitrate = 8000000;

    CHECK(CoInitializeEx(nullptr, COINIT_MULTITHREADED), L"CoInit");
    CHECK(MFStartup(MF_VERSION), L"MFStartup");

    std::vector<uint8_t> bgra; int w=0,h=0;
    if (!LoadBmpBgra(inPath, bgra, w, h)) { wprintf(L"load bmp failed\n"); return 1; }
    if (w%2) w--; if (h%2) h--;
    wprintf(L"loaded %dx%d\n", w, h);

    // Create H.264 encoder MFT (prefer software sync MFT for simplicity/correctness first).
    MFT_REGISTER_TYPE_INFO ot = { MFMediaType_Video, MFVideoFormat_H264 };
    IMFActivate** act=nullptr; UINT32 cnt=0;
    CHECK(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_SYNCMFT|MFT_ENUM_FLAG_SORTANDFILTER|MFT_ENUM_FLAG_TRANSCODE_ONLY, nullptr, &ot, &act, &cnt), L"MFTEnumEx");
    if (cnt==0) { wprintf(L"no sw encoder\n"); return 1; }
    ComPtr<IMFTransform> mft;
    CHECK(act[0]->ActivateObject(IID_PPV_ARGS(&mft)), L"Activate");
    for (UINT32 i=0;i<cnt;++i) act[i]->Release();
    CoTaskMemFree(act);

    // Output type (H.264) must be set before input type.
    ComPtr<IMFMediaType> outType;
    CHECK(MFCreateMediaType(&outType), L"outType");
    outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    outType->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, w, h);
    MFSetAttributeRatio(outType.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(outType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    outType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    outType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
    CHECK(mft->SetOutputType(0, outType.Get(), 0), L"SetOutputType");

    ComPtr<IMFMediaType> inType;
    CHECK(MFCreateMediaType(&inType), L"inType");
    inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, w, h);
    MFSetAttributeRatio(inType.Get(), MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(inType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    inType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    CHECK(mft->SetInputType(0, inType.Get(), 0), L"SetInputType");

    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

    std::vector<uint8_t> nv12((size_t)w*h*3/2);
    BgraToNv12(bgra.data(), w, h, nv12.data());

    FILE* out = _wfopen(outPath, L"wb");
    if (!out) { wprintf(L"open out failed\n"); return 1; }

    LONGLONG dur = 10000000LL / fps;
    LONGLONG pts = 0;
    size_t totalOut = 0;

    auto drainOutput = [&]() -> HRESULT {
        for (;;) {
            MFT_OUTPUT_STREAM_INFO si={};
            mft->GetOutputStreamInfo(0,&si);
            ComPtr<IMFMediaBuffer> buf;
            MFCreateMemoryBuffer(si.cbSize? si.cbSize : (DWORD)(w*h), &buf);
            ComPtr<IMFSample> samp; MFCreateSample(&samp); samp->AddBuffer(buf.Get());
            MFT_OUTPUT_DATA_BUFFER odb={}; odb.pSample=samp.Get();
            DWORD status=0;
            HRESULT hr = mft->ProcessOutput(0,1,&odb,&status);
            if (hr==MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
            if (FAILED(hr)) return hr;
            ComPtr<IMFMediaBuffer> ob; odb.pSample->ConvertToContiguousBuffer(&ob);
            BYTE* pb=nullptr; DWORD cur=0;
            ob->Lock(&pb,nullptr,&cur);
            fwrite(pb,1,cur,out); totalOut+=cur;
            ob->Unlock();
            if (odb.pEvents) odb.pEvents->Release();
        }
    };

    for (int fi=0; fi<frames; ++fi) {
        ComPtr<IMFMediaBuffer> buf;
        MFCreateMemoryBuffer((DWORD)nv12.size(), &buf);
        BYTE* pb=nullptr; DWORD mx=0;
        buf->Lock(&pb,&mx,nullptr);
        memcpy(pb, nv12.data(), nv12.size());
        buf->Unlock();
        buf->SetCurrentLength((DWORD)nv12.size());
        ComPtr<IMFSample> samp; MFCreateSample(&samp); samp->AddBuffer(buf.Get());
        samp->SetSampleTime(pts); samp->SetSampleDuration(dur); pts+=dur;
        HRESULT hr = mft->ProcessInput(0, samp.Get(), 0);
        if (hr==MF_E_NOTACCEPTING) { drainOutput(); hr=mft->ProcessInput(0,samp.Get(),0); }
        CHECK(hr, L"ProcessInput");
        CHECK(drainOutput(), L"drainOutput");
    }
    mft->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    drainOutput();
    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);

    fclose(out);
    wprintf(L"wrote %s (%zu bytes) for %d frames @ %dx%d\n", outPath, totalOut, frames, w, h);
    MFShutdown(); CoUninitialize();
    return 0;
}
