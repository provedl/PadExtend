// bench_dll.cpp - measure pure encode throughput (BGRA->NV12->H264) over N frames.
#include "padextend_encoder.h"
#include <cstdio>
#include <cstdint>
#include <vector>
#include <windows.h>
#include <chrono>

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
    const char* path=argc>1?argv[1]:"C:\\ProgramData\\PadExtend\\frame.bmp";
    int N=argc>2?atoi(argv[2]):120;
    std::vector<uint8_t> bgra;int w,h; if(!load(path,bgra,w,h)){printf("load fail\n");return 1;}
    if(w&1)w--; if(h&1)h--;
    PexEncoder e=PexEncoderCreate(w,h,60,12000000,120);
    if(!e){printf("create fail\n");return 1;}
    // warmup
    const uint8_t* o; int ol,kf;
    for(int i=0;i<10;i++) PexEncoderEncode(e,bgra.data(),(int)bgra.size(),&o,&ol,&kf);
    auto t0=std::chrono::high_resolution_clock::now();
    int produced=0; size_t bytes=0;
    for(int i=0;i<N;i++){ int r=PexEncoderEncode(e,bgra.data(),(int)bgra.size(),&o,&ol,&kf); if(ol>0){produced++;bytes+=ol;} }
    auto t1=std::chrono::high_resolution_clock::now();
    double sec=std::chrono::duration<double>(t1-t0).count();
    printf("frame %dx%d: %d calls in %.3fs => %.1f fps encode, produced=%d avg=%zuB\n",
        w,h,N,sec,N/sec,produced, produced?bytes/produced:0);
    PexEncoderDestroy(e);
    return 0;
}
