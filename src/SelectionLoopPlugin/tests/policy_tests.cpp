#include "../selection_policy.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>
using namespace selection_loop;
static unsigned checks = 0;
static void Check(bool condition,const char* message) {
    ++checks; if (!condition) { fprintf(stderr,"FAIL: %s\n",message); exit(1); }
}
static std::string Json(const char* changes = nullptr) {
    (void)changes;
    return "{\"schema\":1,\"wrestlerId\":161,\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"videoFrames\":15,\"motionFrames\":4,\"fps\":30,\"ending\":\"freeze\"}";
}
static std::string Replace(std::string text,const char* old,const char* replacement) {
    size_t where = text.find(old); Check(where != std::string::npos,"test mutation target exists");
    text.replace(where,strlen(old),replacement); return text;
}
static bool Parse(const std::string& text,Policy* value) { return ParsePolicy(text.data(),text.size(),value); }
int main(int argc,char** argv) {
    Policy policy{}; std::string valid = Json();
    Check(Parse(valid,&policy),"valid schema1");
    Check(policy.wrestler_id == 161 && policy.video_frames == 15 && policy.motion_frames == 4,"decoded values");
    Check(Parse(" \n\r\t"+valid+"\n ",&policy),"JSON whitespace");
    Check(Parse("{\"ending\":\"freeze\",\"fps\":30,\"motionFrames\":4,\"videoFrames\":15,\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"wrestlerId\":161,\"schema\":1}",&policy),"field order independent");
    const char* old[] = {"\"schema\":1","\"schema\":1","\"wrestlerId\":161","\"wrestlerId\":161","\"wrestlerId\":161","\"videoFrames\":15","\"motionFrames\":4","\"motionFrames\":4","\"motionFrames\":4","\"fps\":30","\"ending\":\"freeze\"","\"schema\":1","\"schema\":1","\"schema\":1","\"schema\":1","\"schema\":1","\"schema\":1","\"schema\":1"};
    const char* bad[] = {"\"schema\":2","\"schema\":0","\"wrestlerId\":0","\"wrestlerId\":65536","\"wrestlerId\":-1","\"videoFrames\":16","\"motionFrames\":1","\"motionFrames\":1000000","\"motionFrames\":4294967296","\"fps\":60","\"ending\":\"loop\"","\"schema\":1.0","\"schema\":1e0","\"schema\":01","\"schema\":true","\"schema\":\"1\"","\"schema\":null","\"schema\":[1]"};
    for (size_t i=0;i<sizeof(old)/sizeof(*old);++i) Check(!Parse(Replace(valid,old[i],bad[i]),&policy),"invalid schema scalar rejected");
    Check(!Parse(Replace(valid,"0123456789abcdef","0123456789abcdeF"),&policy),"uppercase SHA rejected");
    Check(!Parse(Replace(valid,"\"ending\"","\"end\\u0069ng\""),&policy),"escaped keys rejected");
    Check(!Parse(Replace(valid,"\"ending\"","\"schema\""),&policy),"duplicate keys rejected");
    Check(!Parse(Replace(valid,"\"ending\"","\"unknown\""),&policy),"unknown keys rejected");
    Check(!Parse(valid+"x",&policy),"trailing data rejected");
    Check(!Parse(valid+valid,&policy),"second document rejected");
    Check(!Parse(std::string("\xef\xbb\xbf")+valid,&policy),"BOM rejected");
    Check(!Parse(std::string(kMaximumPolicyBytes+1,' '),&policy),"size bound");
    for (size_t i=0;i<valid.size();++i) Check(!Parse(valid.substr(0,i),&policy),"every truncation rejected");
    for (size_t i=0;i<valid.size();++i) {
        std::string nul = valid; nul[i]=0; Check(!Parse(nul,&policy),"embedded NUL rejected");
    }
    Check(!ParsePolicy(nullptr,1,&policy) && !ParsePolicy(valid.data(),valid.size(),nullptr),"null arguments");
    Check(Parse(valid,&policy),"restore valid policy");
    Binding binding{161,3,18,1,15,1,1,0x140143f0}; Override row{};
    Check(BindPolicy(policy,binding,&row),"exact primary native binding");
    Check(row.loop_end==7 && row.segment_address==binding.segment_address,"prefix range4frames and exactsegment");
    Check(Lookup(&row,1,161,3,18,1,binding.segment_address)==7,"freeze to clean-prefix loop");
    uint64_t original=(1ull<<32)|18;
    Check(Lookup(&row,1,162,3,18,1,binding.segment_address)==original,"other wrestler unchanged");
    Check(Lookup(&row,1,161,4,18,1,binding.segment_address)==original,"other range start unchanged");
    Check(Lookup(&row,1,161,3,19,1,binding.segment_address)==((1ull<<32)|19),"other range end unchanged");
    Check(Lookup(&row,1,161,3,18,0,binding.segment_address)==18,"original loop unchanged");
    Check(Lookup(&row,1,161,3,18,1,binding.segment_address+16)==original,"other segment address unchanged");
    Check(Lookup(nullptr,1,161,3,18,1,binding.segment_address)==original,"null map unchanged");
    Check(Lookup(&row,kMaximumPolicies+1,161,3,18,1,binding.segment_address)==original,"oversize map unchanged");
    for(unsigned field=0;field<8;++field) {
        Binding changed=binding;
        switch(field) { case 0:changed.wrestler_id++;break;case 1:changed.start++;break;case 2:changed.end++;break;case 3:changed.freeze=0;break;case 4:changed.native_frames++;break;case 5:changed.primary_claims=2;break;case 6:changed.all_claims=2;break;case 7:changed.segment_address=0;break; }
        Check(!BindPolicy(policy,changed,&row),"mismatched/aliased source rejected");
    }
    Binding overflow=binding; overflow.start=0xfffffff8u;overflow.end=0xffffffffu;overflow.native_frames=7;
    Check(!BindPolicy(policy,overflow,&row),"overflow/incompatible range rejected");
    std::vector<Override> rows;
    for(uint32_t i=1;i<=256;++i) rows.push_back(Override{i,3,18,1,7,0x1000u+16*i});
    for(uint32_t i=1;i<=256;++i) Check(Lookup(rows.data(),rows.size(),i,3,18,1,0x1000u+16*i)==7,"all256 immutable lookup rows");
    Check(Lookup(rows.data(),rows.size(),257,3,18,1,0x1000)==original,"missing sorted row");
    if(argc==2) {
        FILE* source=fopen(argv[1],"rb"); Check(source!=nullptr,"native sidecar opens");
        char buffer[kMaximumPolicyBytes+1]; size_t size=fread(buffer,1,sizeof(buffer),source); fclose(source);
        Check(ParsePolicy(buffer,size,&policy),"actual managed-produced native freeze sidecar parses");
        Check(policy.wrestler_id==161 && policy.video_frames==15 && policy.motion_frames==4,"actual native fixture values");
    }
    printf("{\"checks\":%u,\"result\":\"PASS\",\"platform\":\"portable-policy-core\"}\n",checks);
    return 0;
}
