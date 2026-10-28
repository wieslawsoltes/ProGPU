#include <windows.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include "dxcapi.h"
#include <filesystem>
#include <fstream>
#include <vector>
#include <string>
#include <cstdio>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
namespace fs=std::filesystem;
static void Write(const fs::path& path, const void* data, size_t size) {
    std::ofstream f(path,std::ios::binary); f.write(static_cast<const char*>(data),size);
    if(!f) throw std::runtime_error("Evidence write failed");
}
template<class T> static void Blob(const fs::path& path, T* blob) {
    if(blob) Write(path,blob->GetBufferPointer(),blob->GetBufferSize());
    else Write(path,"",0);
}
static bool ExactModule(HMODULE module,const fs::path& expected) {
    wchar_t actual[32768];DWORD n=GetModuleFileNameW(module,actual,32768);
    const auto full=fs::absolute(expected).wstring();
    return n>0&&n<32768&&CompareStringOrdinal(actual,static_cast<int>(n),full.c_str(),static_cast<int>(full.size()),TRUE)==CSTR_EQUAL;
}
static int Status(const fs::path& out,HRESULT hr,const char* phase) {
    char text[128];int n=snprintf(text,sizeof(text),"{\"phase\":\"%s\",\"hresult\":\"0x%08lx\"}\n",phase,static_cast<unsigned long>(hr));
    Write(out/L"status.json",text,n); return SUCCEEDED(hr)?0:1;
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=6) return 2;
    const std::wstring kind=argv[1];const fs::path input=argv[2],out=argv[3],compilerDir=argv[5];
    const std::wstring entry=argv[4];
    if(entry!=L"cs_binary_horizontal"&&entry!=L"cs_binary_vertical") return 2;
    fs::create_directories(out);
    std::ifstream f(input,std::ios::binary);std::vector<char> source((std::istreambuf_iterator<char>(f)),{});
    if(source.empty()||source.size()>1024*1024) return 2;
    if(kind==L"fxc") {
        wchar_t system[MAX_PATH];if(!GetSystemDirectoryW(system,MAX_PATH)) return 2;
        HMODULE module=LoadLibraryW((fs::path(system)/L"d3dcompiler_47.dll").c_str());if(!module)return 2;
        auto compile=reinterpret_cast<pD3DCompile>(GetProcAddress(module,"D3DCompile"));
        auto disassemble=reinterpret_cast<pD3DDisassemble>(GetProcAddress(module,"D3DDisassemble"));
        if(!compile||!disassemble||!ExactModule(module,fs::path(system)/L"d3dcompiler_47.dll"))return 2;
        std::string ep(entry.begin(),entry.end());ComPtr<ID3DBlob> code,errors,assembly;
        HRESULT hr=compile(source.data(),source.size(),"ProGPU binary glyph scan shader",nullptr,nullptr,ep.c_str(),"cs_5_1",D3DCOMPILE_ENABLE_STRICTNESS,0,&code,&errors);
        Blob(out/L"errors.bin",errors.Get());if(FAILED(hr)||!code)return Status(out,FAILED(hr)?hr:E_FAIL,"compile");
        Blob(out/L"code.dxbc",code.Get());hr=disassemble(code->GetBufferPointer(),code->GetBufferSize(),0,nullptr,&assembly);
        Blob(out/L"disassembly.txt",assembly.Get());return Status(out,hr,"disassemble");
    }
    if(kind!=L"dxc")return 2;
    HMODULE dxil=LoadLibraryW((compilerDir/L"dxil.dll").c_str());
    HMODULE dxc=LoadLibraryW((compilerDir/L"dxcompiler.dll").c_str());if(!dxil||!dxc||!ExactModule(dxil,compilerDir/L"dxil.dll")||!ExactModule(dxc,compilerDir/L"dxcompiler.dll"))return 2;
    auto create=reinterpret_cast<DxcCreateInstanceProc>(GetProcAddress(dxc,"DxcCreateInstance"));
    auto createValidator=reinterpret_cast<DxcCreateInstanceProc>(GetProcAddress(dxil,"DxcCreateInstance"));
    if(!create||!createValidator)return 2;
    ComPtr<IDxcLibrary> library;ComPtr<IDxcCompiler> compiler;ComPtr<IDxcValidator> validator;
    HRESULT hr=create(CLSID_DxcLibrary,IID_PPV_ARGS(&library));if(FAILED(hr))return Status(out,hr,"library");
    hr=create(CLSID_DxcCompiler,IID_PPV_ARGS(&compiler));if(FAILED(hr))return Status(out,hr,"compiler");
    hr=createValidator(CLSID_DxcValidator,IID_PPV_ARGS(&validator));if(FAILED(hr))return Status(out,hr,"validator");
    ComPtr<IDxcBlobEncoding> text;hr=library->CreateBlobWithEncodingOnHeapCopy(source.data(),static_cast<UINT32>(source.size()),CP_UTF8,&text);
    if(FAILED(hr))return Status(out,hr,"source");
    const wchar_t* flags[]={L"-Ges",L"-Vd",L"-HV",L"2018"};ComPtr<IDxcOperationResult> result;
    hr=compiler->Compile(text.Get(),L"ProGPU binary glyph scan shader",entry.c_str(),L"cs_6_0",flags,4,nullptr,0,nullptr,&result);
    if(FAILED(hr)||!result)return Status(out,FAILED(hr)?hr:E_FAIL,"compile-call");
    ComPtr<IDxcBlobEncoding> errors;result->GetErrorBuffer(&errors);Blob(out/L"errors.bin",errors.Get());
    HRESULT compileStatus=E_FAIL;hr=result->GetStatus(&compileStatus);if(FAILED(hr)||FAILED(compileStatus))return Status(out,FAILED(hr)?hr:compileStatus,"compile");
    ComPtr<IDxcBlob> code;hr=result->GetResult(&code);if(FAILED(hr)||!code)return Status(out,FAILED(hr)?hr:E_FAIL,"result");
    Blob(out/L"code-unvalidated.dxil",code.Get());ComPtr<IDxcOperationResult> validation;
    hr=validator->Validate(code.Get(),DxcValidatorFlags_InPlaceEdit,&validation);
    if(FAILED(hr)||!validation)return Status(out,FAILED(hr)?hr:E_FAIL,"validate-call");
    ComPtr<IDxcBlobEncoding> validationErrors;validation->GetErrorBuffer(&validationErrors);Blob(out/L"validation-errors.bin",validationErrors.Get());
    HRESULT validationStatus=E_FAIL;hr=validation->GetStatus(&validationStatus);if(FAILED(hr)||FAILED(validationStatus))return Status(out,FAILED(hr)?hr:validationStatus,"validate");
    Blob(out/L"code.dxil",code.Get());ComPtr<IDxcBlobEncoding> assembly;
    hr=compiler->Disassemble(code.Get(),&assembly);Blob(out/L"disassembly.txt",assembly.Get());
    return Status(out,hr,"disassemble");
}
