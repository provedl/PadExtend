// padextend_encoder.cpp - Media Foundation H.264 encoder, C ABI.
// 优先使用异步硬件 MFT (AMDh264Encoder)；不可用时回退同步软件 MFT。
// 对外保持同步语义：PexEncoderEncode 投入一帧 BGRA，取出当前可用的 Annex-B AU。
#define PADEXTEND_ENCODER_EXPORTS
#include "padextend_encoder.h"
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <icodecapi.h>
#include <wrl/client.h>
#include <vector>
#include <deque>
#include <mutex>
#include <thread>
#include <cstring>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "strmiids.lib")

using Microsoft::WRL::ComPtr;

namespace {
struct MfGlobalInit {
    bool ok=false;
    MfGlobalInit(){ ok = SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) && SUCCEEDED(MFStartup(MF_VERSION)); }
};
MfGlobalInit g_init;

// 单条行带（row-pair 范围 [jStart,jEnd)）的 BGRA->NV12 转换。
static void Nv12RowRange(const uint8_t* bgra, int w, int h, uint8_t* nv12, int jStart, int jEnd) {
    uint8_t* yP = nv12;
    uint8_t* uvP = nv12 + (size_t)w*h;
    for (int j=jStart; j<jEnd; ++j) {
        const uint8_t* row = bgra + (size_t)j*w*4;
        uint8_t* yr = yP + (size_t)j*w;
        for (int i=0;i<w;++i) {
            const uint8_t* p = row + (size_t)i*4;
            int B=p[0],G=p[1],R=p[2];
            int Y=((66*R+129*G+25*B+128)>>8)+16;
            yr[i]=(uint8_t)(Y<0?0:Y>255?255:Y);
        }
        if ((j & 1)==0) {
            uint8_t* d = uvP + ((size_t)(j/2)*w);
            for (int i=0;i<w;i+=2) {
                const uint8_t* p = row + (size_t)i*4;
                int B=p[0],G=p[1],R=p[2];
                int U=((-38*R-74*G+112*B+128)>>8)+128;
                int V=((112*R-94*G-18*B+128)>>8)+128;
                d[i]  =(uint8_t)(U<0?0:U>255?255:U);
                d[i+1]=(uint8_t)(V<0?0:V>255?255:V);
            }
        }
    }
}

// 多线程并行 BGRA->NV12：按 row-pair 分块，吃满多核。
void BgraToNv12(const uint8_t* bgra, int w, int h, uint8_t* nv12) {
    unsigned hw = std::thread::hardware_concurrency();
    int threads = (int)(hw ? hw : 4);
    if (threads > 8) threads = 8;
    int pairs = h / 2;                 // UV 以 2 行为单位
    if (threads < 1 || pairs < threads*2) { Nv12RowRange(bgra, w, h, nv12, 0, h); return; }
    int pairsPer = (pairs + threads - 1) / threads;
    std::vector<std::thread> pool;
    pool.reserve(threads);
    for (int t=0; t<threads; ++t) {
        int pStart = t*pairsPer;
        int pEnd = (pStart + pairsPer < pairs) ? pStart + pairsPer : pairs;
        if (pStart >= pEnd) break;
        int jStart = pStart*2, jEnd = pEnd*2;
        pool.emplace_back(Nv12RowRange, bgra, w, h, nv12, jStart, jEnd);
    }
    for (auto& th : pool) th.join();
}
}

struct Encoder {
    ComPtr<IMFTransform> mft;
    ComPtr<IMFMediaEventGenerator> gen; // async only
    bool async=false;
    int w=0,h=0,fps=30;
    LONGLONG dur=0, pts=0;
    std::vector<uint8_t> nv12;
    std::vector<uint8_t> outBuf;   // last produced access unit (Annex-B)
    std::vector<uint8_t> config;   // SPS+PPS (Annex-B)
    bool configCaptured=false;
    bool lastKey=false;
};

static void ConfigureCodecApi(IMFTransform* mft, int keyInterval) {
    ComPtr<ICodecAPI> codec;
    if (SUCCEEDED(mft->QueryInterface(IID_PPV_ARGS(&codec)))) {
        VARIANT rc; rc.vt=VT_UI4; rc.ulVal=eAVEncCommonRateControlMode_CBR;
        codec->SetValue(&CODECAPI_AVEncCommonRateControlMode, &rc);
        if (keyInterval > 0) { VARIANT v; v.vt=VT_UI4; v.ulVal=(ULONG)keyInterval; codec->SetValue(&CODECAPI_AVEncMPVGOPSize, &v); }
        VARIANT ll; ll.vt=VT_BOOL; ll.boolVal=VARIANT_TRUE; codec->SetValue(&CODECAPI_AVLowLatencyMode, &ll);
    }
}

static bool SetTypes(Encoder* e, int bitrate) {
    ComPtr<IMFMediaType> outType;
    MFCreateMediaType(&outType);
    outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    outType->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, e->w, e->h);
    MFSetAttributeRatio(outType.Get(), MF_MT_FRAME_RATE, e->fps, 1);
    MFSetAttributeRatio(outType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    outType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    outType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
    if (FAILED(e->mft->SetOutputType(0, outType.Get(), 0))) return false;

    ComPtr<IMFMediaType> inType;
    MFCreateMediaType(&inType);
    inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, e->w, e->h);
    MFSetAttributeRatio(inType.Get(), MF_MT_FRAME_RATE, e->fps, 1);
    MFSetAttributeRatio(inType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    inType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(e->mft->SetInputType(0, inType.Get(), 0))) return false;
    return true;
}

static bool CreateMft(Encoder* e, int bitrate, int keyInterval) {
    MFT_REGISTER_TYPE_INFO ot = { MFMediaType_Video, MFVideoFormat_H264 };
    for (int pass=0; pass<2; ++pass) {
        UINT32 flags = MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_TRANSCODE_ONLY;
        flags |= (pass==0)? MFT_ENUM_FLAG_HARDWARE : MFT_ENUM_FLAG_SYNCMFT;
        IMFActivate** act=nullptr; UINT32 cnt=0;
        if (FAILED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, nullptr, &ot, &act, &cnt)) || cnt==0) {
            if (act) CoTaskMemFree(act);
            continue;
        }
        ComPtr<IMFTransform> mft;
        HRESULT hr = act[0]->ActivateObject(IID_PPV_ARGS(&mft));
        for (UINT32 i=0;i<cnt;++i) act[i]->Release();
        CoTaskMemFree(act);
        if (FAILED(hr)) continue;

        bool isAsync=false;
        ComPtr<IMFAttributes> attrs;
        if (SUCCEEDED(mft->GetAttributes(&attrs))) {
            UINT32 a=0; attrs->GetUINT32(MF_TRANSFORM_ASYNC, &a); isAsync = (a!=0);
            if (isAsync) attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
        }

        e->mft = mft;
        e->async = isAsync;
        // 异步 MFT：输出类型必须先设，且某些实现要求在设类型前拿 event generator。
        if (!SetTypes(e, bitrate)) { e->mft.Reset(); continue; }
        ConfigureCodecApi(mft.Get(), keyInterval);

        if (isAsync) {
            if (FAILED(mft->QueryInterface(IID_PPV_ARGS(&e->gen)))) { e->mft.Reset(); e->gen.Reset(); continue; }
        }
        mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        return true;
    }
    return false;
}

static void CaptureConfigFromAu(Encoder* e, const uint8_t* au, int len) {
    if (e->configCaptured) return;
    std::vector<uint8_t> cfg;
    int i=0;
    while (i+4 <= len) {
        int sc=-1, scLen=0;
        for (int j=i;j+3<len;++j){
            if (au[j]==0&&au[j+1]==0&&au[j+2]==1){ sc=j; scLen=3; break; }
            if (j+4<len && au[j]==0&&au[j+1]==0&&au[j+2]==0&&au[j+3]==1){ sc=j; scLen=4; break; }
        }
        if (sc<0) break;
        int nalStart=sc+scLen;
        int next=len;
        for (int j=nalStart;j+3<len;++j){
            if (au[j]==0&&au[j+1]==0&&au[j+2]==1){ next=j; break; }
            if (j+4<len && au[j]==0&&au[j+1]==0&&au[j+2]==0&&au[j+3]==1){ next=j; break; }
        }
        int type = au[nalStart]&0x1F;
        if (type==7||type==8) cfg.insert(cfg.end(), au+sc, au+next);
        i=next;
    }
    if (!cfg.empty()){ e->config=cfg; e->configCaptured=true; }
}

static ComPtr<IMFSample> MakeInputSample(Encoder* e, const uint8_t* bgra) {
    BgraToNv12(bgra, e->w, e->h, e->nv12.data());
    ComPtr<IMFMediaBuffer> buf;
    MFCreateMemoryBuffer((DWORD)e->nv12.size(), &buf);
    BYTE* pb=nullptr; DWORD mx=0;
    buf->Lock(&pb,&mx,nullptr); memcpy(pb, e->nv12.data(), e->nv12.size()); buf->Unlock();
    buf->SetCurrentLength((DWORD)e->nv12.size());
    ComPtr<IMFSample> samp; MFCreateSample(&samp); samp->AddBuffer(buf.Get());
    samp->SetSampleTime(e->pts); samp->SetSampleDuration(e->dur); e->pts+=e->dur;
    return samp;
}

static bool ReadOutput(Encoder* e, bool& key) {
    MFT_OUTPUT_STREAM_INFO si={}; e->mft->GetOutputStreamInfo(0,&si);
    bool providesSamples = (si.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES|MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES))!=0;
    ComPtr<IMFSample> os;
    ComPtr<IMFMediaBuffer> ob;
    if (!providesSamples) {
        MFCreateMemoryBuffer(si.cbSize? si.cbSize : (DWORD)(e->w*e->h), &ob);
        MFCreateSample(&os); os->AddBuffer(ob.Get());
    }
    MFT_OUTPUT_DATA_BUFFER odb={}; odb.pSample = providesSamples? nullptr : os.Get();
    DWORD status=0;
    HRESULT hr = e->mft->ProcessOutput(0,1,&odb,&status);
    if (hr==MF_E_TRANSFORM_NEED_MORE_INPUT) { if(odb.pEvents)odb.pEvents->Release(); return false; }
    if (FAILED(hr)) { if(odb.pEvents)odb.pEvents->Release(); return false; }
    ComPtr<IMFSample> produced = providesSamples ? ComPtr<IMFSample>(odb.pSample) : os;
    if (!produced) { if(odb.pEvents)odb.pEvents->Release(); return false; }
    UINT32 clean=0;
    if (SUCCEEDED(produced->GetUINT32(MFSampleExtension_CleanPoint,&clean)) && clean) key=true;
    ComPtr<IMFMediaBuffer> cont; produced->ConvertToContiguousBuffer(&cont);
    BYTE* cp=nullptr; DWORD cl=0; cont->Lock(&cp,nullptr,&cl);
    size_t off=e->outBuf.size(); e->outBuf.resize(off+cl); memcpy(e->outBuf.data()+off,cp,cl);
    cont->Unlock();
    if (providesSamples && odb.pSample) odb.pSample->Release();
    if (odb.pEvents) odb.pEvents->Release();
    return true;
}

extern "C" {

PEX_API PexEncoder PexEncoderCreate(int width,int height,int fps,int bitrate,int keyIntervalFrames){
    if (!g_init.ok) return nullptr;
    if (width<=0||height<=0||(width&1)||(height&1)) return nullptr;
    Encoder* e = new Encoder();
    e->w=width; e->h=height; e->fps=(fps>0?fps:30);
    e->dur = 10000000LL / e->fps;
    e->nv12.resize((size_t)width*height*3/2);
    if (!CreateMft(e, bitrate>0?bitrate:8000000, keyIntervalFrames)) { delete e; return nullptr; }
    return e;
}

// 异步 MFT：驱动事件，拿到 NeedInput 就喂、HaveOutput 就取；一次调用处理一帧并尽量取出所有就绪输出。
static int EncodeAsync(Encoder* e, const uint8_t* bgra, const uint8_t** outPtr, int* outLen, int* isKeyframe){
    e->outBuf.clear();
    bool key=false;
    bool fed=false;
    // 最多处理若干事件，直到喂入本帧且取出至少一个输出（或无更多输出）。
    for (int guard=0; guard<32; ++guard) {
        ComPtr<IMFMediaEvent> ev;
        HRESULT hr = e->gen->GetEvent(0, &ev); // 阻塞直到事件
        if (FAILED(hr)) break;
        MediaEventType met=0; ev->GetType(&met);
        if (met==METransformNeedInput) {
            if (!fed) {
                ComPtr<IMFSample> s = MakeInputSample(e, bgra);
                if (SUCCEEDED(e->mft->ProcessInput(0, s.Get(), 0))) fed=true;
            } else {
                // 已喂过本帧：再喂会打乱节奏，发 drain 促使输出，然后退出。
                // 这里直接结束，剩余输出下次取。
                break;
            }
        } else if (met==METransformHaveOutput) {
            ReadOutput(e, key);
            if (fed) break; // 喂过且取到输出，结束本次
        }
    }
    if (!e->outBuf.empty()) {
        CaptureConfigFromAu(e, e->outBuf.data(), (int)e->outBuf.size());
        if (!key) for (int j=0;j+4<(int)e->outBuf.size();++j){ if(e->outBuf[j]==0&&e->outBuf[j+1]==0&&e->outBuf[j+2]==0&&e->outBuf[j+3]==1){ if((e->outBuf[j+4]&0x1F)==5){key=true;break;} } }
        *outPtr=e->outBuf.data(); *outLen=(int)e->outBuf.size();
        if(isKeyframe)*isKeyframe=key?1:0;
    }
    return 0;
}

static int EncodeSync(Encoder* e, const uint8_t* bgra, const uint8_t** outPtr, int* outLen, int* isKeyframe){
    ComPtr<IMFSample> samp = MakeInputSample(e, bgra);
    HRESULT hr = e->mft->ProcessInput(0, samp.Get(), 0);
    e->outBuf.clear();
    bool key=false;
    for(;;){ if(!ReadOutput(e,key)) break; }
    if (hr==MF_E_NOTACCEPTING) e->mft->ProcessInput(0, samp.Get(), 0);
    if (!e->outBuf.empty()) {
        CaptureConfigFromAu(e, e->outBuf.data(), (int)e->outBuf.size());
        if (!key) for (int j=0;j+4<(int)e->outBuf.size();++j){ if(e->outBuf[j]==0&&e->outBuf[j+1]==0&&e->outBuf[j+2]==0&&e->outBuf[j+3]==1){ if((e->outBuf[j+4]&0x1F)==5){key=true;break;} } }
        *outPtr=e->outBuf.data(); *outLen=(int)e->outBuf.size();
        if(isKeyframe)*isKeyframe=key?1:0;
    }
    return 0;
}

PEX_API int PexEncoderEncode(PexEncoder enc,const uint8_t* bgra,int bgraLen,
                             const uint8_t** outPtr,int* outLen,int* isKeyframe){
    Encoder* e=(Encoder*)enc;
    if(!e||!bgra) return -1;
    if(bgraLen < e->w*e->h*4) return -2;
    *outPtr=nullptr; *outLen=0; if(isKeyframe)*isKeyframe=0;
    return e->async ? EncodeAsync(e,bgra,outPtr,outLen,isKeyframe)
                    : EncodeSync(e,bgra,outPtr,outLen,isKeyframe);
}

PEX_API int PexEncoderGetConfig(PexEncoder enc,const uint8_t** outPtr,int* outLen){
    Encoder* e=(Encoder*)enc;
    if(!e||!e->configCaptured) return -1;
    *outPtr=e->config.data(); *outLen=(int)e->config.size();
    return 0;
}

PEX_API void PexEncoderForceKeyframe(PexEncoder enc){
    Encoder* e=(Encoder*)enc; if(!e) return;
    ComPtr<ICodecAPI> codec;
    if (SUCCEEDED(e->mft.As(&codec))) { VARIANT v; v.vt=VT_UI4; v.ulVal=1; codec->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &v); }
}

PEX_API void PexEncoderDestroy(PexEncoder enc){
    Encoder* e=(Encoder*)enc; if(!e) return;
    if (e->mft){
        e->mft->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN,0);
        e->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM,0);
        e->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING,0);
    }
    delete e;
}

} // extern "C"
