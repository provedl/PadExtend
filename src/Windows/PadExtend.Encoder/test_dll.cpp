// test_dll.cpp - exercise the encoder DLL C ABI against frame.bmp.
#include "padextend_encoder.h"
#include <cstdio>
#include <cstdint>
#include <vector>
#include <windows.h>

#pragma pack(push,1)
struct BFH{uint16_t t;uint32_t s;uint16_t r1,r2;uint32_t off;};
struct BIH{uint32_t sz;int32_t w,h;uint16_t pl,bc;uint32_t comp,si;int32_t x,y;uint32_t cu,ci;};
#pragma pack(pop)

static bool load(const char* p, std::vector<uint8_t>& out,int& w,int& h){
    FILE* f=fopen(p,"rb"); if(!f)return false;
    BFH fh; BIH ih; fread(&fh,sizeof fh,1,f); fread(&ih,sizeof ih,1,f);
    if(ih.bc!=32){fclose(f);return false;}
    w=ih.w; h=(ih.h<0?-ih.h:ih.h); bool bu=ih.h>0;
    std::vector<uint8_t> raw((size_t)w*h*4);
    fseek(f,fh.off,SEEK_SET); fread(raw.data(),1,raw.size(),f); fclose(f);
    out.resize((size_t)w*h*4);
    for(int r=0;r<h;++r){int s=bu?(h-1-r):r; memcpy(&out[(size_t)r*w*4],&raw[(size_t)s*w*4],(size_t)w*4);}
    return true;
}

int main(int argc,char**argv){
    const char* path = argc>1?argv[1]:"C:\\ProgramData\\PadExtend\\frame.bmp";
    std::vector<uint8_t> bgra; int w,h;
    if(!load(path,bgra,w,h)){printf("load failed\n");return 1;}
    if(w&1)w--; if(h&1)h--;
    printf("frame %dx%d\n",w,h);
    PexEncoder e=PexEncoderCreate(w,h,30,8000000,60);
    if(!e){printf("create failed\n");return 1;}
    int keyframes=0,total=0; size_t bytes=0; bool gotConfig=false;
    for(int i=0;i<60;++i){
        if(i==30) PexEncoderForceKeyframe(e);
        const uint8_t* out=nullptr; int ol=0, kf=0;
        int r=PexEncoderEncode(e,bgra.data(),(int)bgra.size(),&out,&ol,&kf);
        if(r!=0){printf("encode err %d at %d\n",r,i);break;}
        if(ol>0){total++; bytes+=ol; if(kf)keyframes++;}
        if(!gotConfig){const uint8_t* c=nullptr;int cl=0; if(PexEncoderGetConfig(e,&c,&cl)==0){gotConfig=true; printf("config captured: %d bytes\n",cl);}}
    }
    printf("encoded AUs=%d keyframes=%d totalBytes=%zu avg=%zu\n",total,keyframes,bytes, total?bytes/total:0);
    PexEncoderDestroy(e);
    printf("done\n");
    return 0;
}
