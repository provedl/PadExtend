// MF H.264 encoder probe: enumerate available H.264 encoder MFTs (hardware + software).
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <cstdio>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "ole32.lib")

static void PrintGuidName(const wchar_t* label, REFGUID g) {
    wchar_t buf[64];
    StringFromGUID2(g, buf, 64);
    wprintf(L"  %s = %s\n", label, buf);
}

int main() {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) { wprintf(L"CoInit failed 0x%08X\n", hr); return 1; }
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) { wprintf(L"MFStartup failed 0x%08X\n", hr); return 1; }

    MFT_REGISTER_TYPE_INFO outType = { MFMediaType_Video, MFVideoFormat_H264 };

    for (int pass = 0; pass < 2; ++pass) {
        UINT32 flags = MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_TRANSCODE_ONLY;
        flags |= (pass == 0) ? MFT_ENUM_FLAG_HARDWARE : MFT_ENUM_FLAG_SYNCMFT;
        wprintf(L"=== Pass %d (%s) ===\n", pass, pass == 0 ? L"HARDWARE" : L"SOFTWARE/SYNC");

        IMFActivate** ppActivate = nullptr;
        UINT32 count = 0;
        hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, nullptr, &outType, &ppActivate, &count);
        if (FAILED(hr)) { wprintf(L"  MFTEnumEx failed 0x%08X\n", hr); continue; }
        wprintf(L"  found %u encoder(s)\n", count);
        for (UINT32 i = 0; i < count; ++i) {
            LPWSTR name = nullptr; UINT32 len = 0;
            if (SUCCEEDED(ppActivate[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &name, &len))) {
                wprintf(L"    [%u] %s\n", i, name);
                CoTaskMemFree(name);
            } else {
                wprintf(L"    [%u] (no name)\n", i);
            }
            ppActivate[i]->Release();
        }
        if (ppActivate) CoTaskMemFree(ppActivate);
    }

    MFShutdown();
    CoUninitialize();
    wprintf(L"done.\n");
    return 0;
}
