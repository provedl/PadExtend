// probe2.cpp - inspect AMD H.264 encoder MFT: async flag, D3D-aware, input/output formats.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <cstdio>
#pragma comment(lib,"mfplat.lib")
#pragma comment(lib,"mfuuid.lib")
#pragma comment(lib,"mf.lib")
#pragma comment(lib,"ole32.lib")

int main(){
    CoInitializeEx(nullptr,COINIT_MULTITHREADED); MFStartup(MF_VERSION);
    MFT_REGISTER_TYPE_INFO ot={MFMediaType_Video,MFVideoFormat_H264};
    IMFActivate** act=nullptr; UINT32 cnt=0;
    HRESULT hr=MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE|MFT_ENUM_FLAG_SORTANDFILTER|MFT_ENUM_FLAG_TRANSCODE_ONLY, nullptr, &ot, &act, &cnt);
    wprintf(L"hw encoders: %u (hr=0x%08X)\n", cnt, hr);
    if(cnt==0) return 0;
    LPWSTR nm=nullptr; UINT32 l=0;
    act[0]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute,&nm,&l);
    wprintf(L"name: %s\n", nm?nm:L"?"); if(nm)CoTaskMemFree(nm);
    IMFTransform* mft=nullptr;
    hr=act[0]->ActivateObject(IID_PPV_ARGS(&mft));
    wprintf(L"activate hr=0x%08X\n",hr);
    if(SUCCEEDED(hr)){
        IMFAttributes* a=nullptr;
        if(SUCCEEDED(mft->GetAttributes(&a))){
            UINT32 async=0; a->GetUINT32(MF_TRANSFORM_ASYNC,&async);
            UINT32 d3d=0; a->GetUINT32(MF_SA_D3D11_AWARE,&d3d);
            wprintf(L"MF_TRANSFORM_ASYNC=%u  MF_SA_D3D11_AWARE=%u\n", async, d3d);
            a->Release();
        }
        // enumerate input types
        for(DWORD i=0;;++i){ IMFMediaType* t=nullptr; if(mft->GetInputAvailableType(0,i,&t)!=S_OK) break; GUID sub; t->GetGUID(MF_MT_SUBTYPE,&sub); wprintf(L"  in[%u] subtype data1=0x%08X\n", i, sub.Data1); t->Release(); if(i>8)break; }
    }
    for(UINT32 i=0;i<cnt;++i) act[i]->Release(); CoTaskMemFree(act);
    MFShutdown(); CoUninitialize(); return 0;
}
