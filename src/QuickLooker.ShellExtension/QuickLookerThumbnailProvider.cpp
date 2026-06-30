#include <windows.h>
#ifdef __MINGW32__
#include <initguid.h>
#endif
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <strsafe.h>
#include <thumbcache.h>
#include <wincodec.h>

#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "shell32.lib")

namespace
{
    constexpr wchar_t kClsidText[] = L"{5BB47C0C-7A24-4ADC-9F23-072422343BA7}";
    constexpr wchar_t kThumbnailHandlerGuid[] = L"{E357FCCD-A995-4576-B01F-234630154E96}";
    constexpr DWORD kThumbnailerTimeoutMs = 10000;

    const CLSID CLSID_QuickLookerThumbnailProvider =
    { 0x5bb47c0c, 0x7a24, 0x4adc, { 0x9f, 0x23, 0x07, 0x24, 0x22, 0x34, 0x3b, 0xa7 } };

    HINSTANCE g_instance = nullptr;
    long g_moduleReferences = 0;

    template <typename T>
    void SafeRelease(T** value)
    {
        if (*value != nullptr)
        {
            (*value)->Release();
            *value = nullptr;
        }
    }

    void AddModuleReference()
    {
        InterlockedIncrement(&g_moduleReferences);
    }

    void ReleaseModuleReference()
    {
        InterlockedDecrement(&g_moduleReferences);
    }

    HRESULT HResultFromWin32LastError()
    {
        const DWORD error = GetLastError();
        return error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error);
    }

    HRESULT DuplicateString(PCWSTR source, PWSTR* target)
    {
        if (target == nullptr)
        {
            return E_POINTER;
        }

        *target = nullptr;

        if (source == nullptr)
        {
            return E_INVALIDARG;
        }

        const size_t length = wcslen(source) + 1;
        auto copy = static_cast<PWSTR>(CoTaskMemAlloc(length * sizeof(wchar_t)));

        if (copy == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = StringCchCopyW(copy, length, source);

        if (FAILED(hr))
        {
            CoTaskMemFree(copy);
            return hr;
        }

        *target = copy;
        return S_OK;
    }

    HRESULT CreateTempPath(PWSTR path, DWORD pathLength)
    {
        wchar_t tempDirectory[MAX_PATH] = {};

        if (GetTempPathW(ARRAYSIZE(tempDirectory), tempDirectory) == 0)
        {
            return HResultFromWin32LastError();
        }

        if (GetTempFileNameW(tempDirectory, L"qlk", 0, path) == 0)
        {
            return HResultFromWin32LastError();
        }

        UNREFERENCED_PARAMETER(pathLength);
        return S_OK;
    }

    HRESULT CopyStreamToFile(IStream* stream, PCWSTR targetPath)
    {
        if (stream == nullptr)
        {
            return E_POINTER;
        }

        LARGE_INTEGER start = {};
        stream->Seek(start, STREAM_SEEK_SET, nullptr);

        HANDLE file = CreateFileW(
            targetPath,
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_TEMPORARY,
            nullptr);

        if (file == INVALID_HANDLE_VALUE)
        {
            return HResultFromWin32LastError();
        }

        HRESULT hr = S_OK;
        BYTE buffer[64 * 1024] = {};

        for (;;)
        {
            ULONG bytesRead = 0;
            hr = stream->Read(buffer, static_cast<ULONG>(sizeof(buffer)), &bytesRead);

            if (FAILED(hr) || bytesRead == 0)
            {
                break;
            }

            DWORD bytesWritten = 0;

            if (!WriteFile(file, buffer, bytesRead, &bytesWritten, nullptr) || bytesWritten != bytesRead)
            {
                hr = HResultFromWin32LastError();
                break;
            }
        }

        CloseHandle(file);
        return FAILED(hr) ? hr : S_OK;
    }

    std::wstring QuoteArgument(PCWSTR value)
    {
        std::wstring result = L"\"";

        for (PCWSTR current = value; *current != L'\0'; ++current)
        {
            if (*current == L'"')
            {
                result += L'\\';
            }

            result += *current;
        }

        result += L'"';
        return result;
    }

    HRESULT GetSiblingPath(PCWSTR fileName, PWSTR path, DWORD pathLength)
    {
        if (GetModuleFileNameW(g_instance, path, pathLength) == 0)
        {
            return HResultFromWin32LastError();
        }

        if (!PathRemoveFileSpecW(path))
        {
            return E_FAIL;
        }

        if (!PathAppendW(path, fileName))
        {
            return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        }

        return S_OK;
    }

    HRESULT ReadThumbnailerPathFromRegistry(PWSTR path, DWORD pathLength)
    {
        DWORD type = REG_SZ;
        DWORD bytes = pathLength * sizeof(wchar_t);
        const LSTATUS status = RegGetValueW(
            HKEY_CURRENT_USER,
            L"Software\\PicPreview",
            L"ThumbnailerPath",
            RRF_RT_REG_SZ,
            &type,
            path,
            &bytes);

        return status == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(status);
    }

    HRESULT GetThumbnailerPath(PWSTR path, DWORD pathLength)
    {
        HRESULT hr = ReadThumbnailerPathFromRegistry(path, pathLength);

        if (SUCCEEDED(hr) && PathFileExistsW(path))
        {
            return S_OK;
        }

        hr = GetSiblingPath(L"QuickLooker.Thumbnailer.exe", path, pathLength);

        if (FAILED(hr))
        {
            return hr;
        }

        return PathFileExistsW(path) ? S_OK : HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    HRESULT RunThumbnailer(PCWSTR inputPath, PCWSTR outputPath, UINT size)
    {
        wchar_t thumbnailerPath[MAX_PATH] = {};
        HRESULT hr = GetThumbnailerPath(thumbnailerPath, ARRAYSIZE(thumbnailerPath));

        if (FAILED(hr))
        {
            return hr;
        }

        std::wstring commandLine =
            QuoteArgument(thumbnailerPath) +
            L" thumbnail --input " +
            QuoteArgument(inputPath) +
            L" --output " +
            QuoteArgument(outputPath) +
            L" --size " +
            std::to_wstring(size);

        std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
        mutableCommandLine.push_back(L'\0');

        STARTUPINFOW startupInfo = {};
        startupInfo.cb = sizeof(startupInfo);
        startupInfo.dwFlags = STARTF_USESHOWWINDOW;
        startupInfo.wShowWindow = SW_HIDE;

        PROCESS_INFORMATION processInformation = {};

        if (!CreateProcessW(
            nullptr,
            mutableCommandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_NO_WINDOW,
            nullptr,
            nullptr,
            &startupInfo,
            &processInformation))
        {
            return HResultFromWin32LastError();
        }

        const DWORD waitResult = WaitForSingleObject(processInformation.hProcess, kThumbnailerTimeoutMs);

        if (waitResult == WAIT_TIMEOUT)
        {
            TerminateProcess(processInformation.hProcess, 2);
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
            return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }

        DWORD exitCode = 1;
        GetExitCodeProcess(processInformation.hProcess, &exitCode);

        CloseHandle(processInformation.hThread);
        CloseHandle(processInformation.hProcess);

        return exitCode == 0 ? S_OK : E_FAIL;
    }

    HRESULT LoadPngAsBitmap(PCWSTR path, HBITMAP* bitmap)
    {
        if (bitmap == nullptr)
        {
            return E_POINTER;
        }

        *bitmap = nullptr;

        IWICImagingFactory* factory = nullptr;
        IWICBitmapDecoder* decoder = nullptr;
        IWICBitmapFrameDecode* frame = nullptr;
        IWICFormatConverter* converter = nullptr;

        HRESULT hr = CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&factory));

        if (SUCCEEDED(hr))
        {
            hr = factory->CreateDecoderFromFilename(
                path,
                nullptr,
                GENERIC_READ,
                WICDecodeMetadataCacheOnDemand,
                &decoder);
        }

        if (SUCCEEDED(hr))
        {
            hr = decoder->GetFrame(0, &frame);
        }

        if (SUCCEEDED(hr))
        {
            hr = factory->CreateFormatConverter(&converter);
        }

        if (SUCCEEDED(hr))
        {
            hr = converter->Initialize(
                frame,
                GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherTypeNone,
                nullptr,
                0.0,
                WICBitmapPaletteTypeMedianCut);
        }

        UINT width = 0;
        UINT height = 0;

        if (SUCCEEDED(hr))
        {
            hr = converter->GetSize(&width, &height);
        }

        void* bits = nullptr;

        if (SUCCEEDED(hr))
        {
            BITMAPINFO bitmapInfo = {};
            bitmapInfo.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bitmapInfo.bmiHeader.biWidth = static_cast<LONG>(width);
            bitmapInfo.bmiHeader.biHeight = -static_cast<LONG>(height);
            bitmapInfo.bmiHeader.biPlanes = 1;
            bitmapInfo.bmiHeader.biBitCount = 32;
            bitmapInfo.bmiHeader.biCompression = BI_RGB;

            *bitmap = CreateDIBSection(
                nullptr,
                &bitmapInfo,
                DIB_RGB_COLORS,
                &bits,
                nullptr,
                0);

            if (*bitmap == nullptr)
            {
                hr = HResultFromWin32LastError();
            }
        }

        if (SUCCEEDED(hr))
        {
            hr = converter->CopyPixels(
                nullptr,
                width * 4,
                width * height * 4,
                static_cast<BYTE*>(bits));
        }

        if (FAILED(hr) && *bitmap != nullptr)
        {
            DeleteObject(*bitmap);
            *bitmap = nullptr;
        }

        SafeRelease(&converter);
        SafeRelease(&frame);
        SafeRelease(&decoder);
        SafeRelease(&factory);

        return hr;
    }

    HRESULT SetRegistryString(HKEY root, PCWSTR subKey, PCWSTR valueName, PCWSTR value)
    {
        HKEY key = nullptr;
        const LSTATUS createStatus = RegCreateKeyExW(
            root,
            subKey,
            0,
            nullptr,
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            nullptr,
            &key,
            nullptr);

        if (createStatus != ERROR_SUCCESS)
        {
            return HRESULT_FROM_WIN32(createStatus);
        }

        const DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
        const LSTATUS setStatus = RegSetValueExW(
            key,
            valueName,
            0,
            REG_SZ,
            reinterpret_cast<const BYTE*>(value),
            bytes);

        RegCloseKey(key);
        return setStatus == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(setStatus);
    }

    HRESULT DeleteRegistryTree(HKEY root, PCWSTR subKey)
    {
        const LSTATUS status = RegDeleteTreeW(root, subKey);
        return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND
            ? S_OK
            : HRESULT_FROM_WIN32(status);
    }

    HRESULT RegisterExtension(PCWSTR extension)
    {
        wchar_t subKey[256] = {};
        HRESULT hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\%s\\ShellEx\\%s",
            extension,
            kThumbnailHandlerGuid);

        if (FAILED(hr))
        {
            return hr;
        }

        hr = SetRegistryString(HKEY_CURRENT_USER, subKey, nullptr, kClsidText);

        if (FAILED(hr))
        {
            return hr;
        }

        hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\SystemFileAssociations\\%s\\ShellEx\\%s",
            extension,
            kThumbnailHandlerGuid);

        if (FAILED(hr))
        {
            return hr;
        }

        hr = SetRegistryString(HKEY_CURRENT_USER, subKey, nullptr, kClsidText);

        if (FAILED(hr))
        {
            return hr;
        }

        wchar_t progId[128] = {};
        DWORD progIdBytes = sizeof(progId);
        const LSTATUS progIdStatus = RegGetValueW(
            HKEY_CLASSES_ROOT,
            extension,
            nullptr,
            RRF_RT_REG_SZ,
            nullptr,
            progId,
            &progIdBytes);

        if (progIdStatus != ERROR_SUCCESS || progId[0] == L'\0')
        {
            return S_OK;
        }

        hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\%s\\ShellEx\\%s",
            progId,
            kThumbnailHandlerGuid);

        if (FAILED(hr))
        {
            return hr;
        }

        hr = SetRegistryString(HKEY_CURRENT_USER, subKey, nullptr, kClsidText);

        if (FAILED(hr))
        {
            return hr;
        }

        wchar_t progIdClsidKey[192] = {};
        hr = StringCchPrintfW(progIdClsidKey, ARRAYSIZE(progIdClsidKey), L"%s\\CLSID", progId);

        if (FAILED(hr))
        {
            return hr;
        }

        wchar_t fileTypeClsid[64] = {};
        DWORD fileTypeClsidBytes = sizeof(fileTypeClsid);
        const LSTATUS fileTypeClsidStatus = RegGetValueW(
            HKEY_CLASSES_ROOT,
            progIdClsidKey,
            nullptr,
            RRF_RT_REG_SZ,
            nullptr,
            fileTypeClsid,
            &fileTypeClsidBytes);

        if (fileTypeClsidStatus != ERROR_SUCCESS || fileTypeClsid[0] == L'\0')
        {
            wchar_t machineProgIdClsidKey[256] = {};
            hr = StringCchPrintfW(machineProgIdClsidKey, ARRAYSIZE(machineProgIdClsidKey), L"Software\\Classes\\%s\\CLSID", progId);

            if (FAILED(hr))
            {
                return hr;
            }

            fileTypeClsidBytes = sizeof(fileTypeClsid);
            const LSTATUS machineFileTypeClsidStatus = RegGetValueW(
                HKEY_LOCAL_MACHINE,
                machineProgIdClsidKey,
                nullptr,
                RRF_RT_REG_SZ,
                nullptr,
                fileTypeClsid,
                &fileTypeClsidBytes);

            if (machineFileTypeClsidStatus != ERROR_SUCCESS || fileTypeClsid[0] == L'\0')
            {
                return S_OK;
            }
        }

        hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\CLSID\\%s\\ShellEx\\%s",
            fileTypeClsid,
            kThumbnailHandlerGuid);

        if (FAILED(hr))
        {
            return hr;
        }

        return SetRegistryString(HKEY_CURRENT_USER, subKey, nullptr, kClsidText);
    }

    HRESULT RegisterApprovedExtension()
    {
        return SetRegistryString(
            HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
            kClsidText,
            L"PicPreview Thumbnail Provider");
    }

    HRESULT UnregisterExtension(PCWSTR extension)
    {
        wchar_t subKey[256] = {};
        HRESULT hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\%s\\ShellEx\\%s",
            extension,
            kThumbnailHandlerGuid);

        if (SUCCEEDED(hr))
        {
            hr = DeleteRegistryTree(HKEY_CURRENT_USER, subKey);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\SystemFileAssociations\\%s\\ShellEx\\%s",
            extension,
            kThumbnailHandlerGuid);

        if (SUCCEEDED(hr))
        {
            hr = DeleteRegistryTree(HKEY_CURRENT_USER, subKey);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        wchar_t progId[128] = {};
        DWORD progIdBytes = sizeof(progId);
        const LSTATUS progIdStatus = RegGetValueW(
            HKEY_CLASSES_ROOT,
            extension,
            nullptr,
            RRF_RT_REG_SZ,
            nullptr,
            progId,
            &progIdBytes);

        if (progIdStatus != ERROR_SUCCESS || progId[0] == L'\0')
        {
            return S_OK;
        }

        hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\%s\\ShellEx\\%s",
            progId,
            kThumbnailHandlerGuid);

        if (SUCCEEDED(hr))
        {
            hr = DeleteRegistryTree(HKEY_CURRENT_USER, subKey);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        wchar_t progIdClsidKey[192] = {};
        hr = StringCchPrintfW(progIdClsidKey, ARRAYSIZE(progIdClsidKey), L"%s\\CLSID", progId);

        if (FAILED(hr))
        {
            return hr;
        }

        wchar_t fileTypeClsid[64] = {};
        DWORD fileTypeClsidBytes = sizeof(fileTypeClsid);
        const LSTATUS fileTypeClsidStatus = RegGetValueW(
            HKEY_CLASSES_ROOT,
            progIdClsidKey,
            nullptr,
            RRF_RT_REG_SZ,
            nullptr,
            fileTypeClsid,
            &fileTypeClsidBytes);

        if (fileTypeClsidStatus != ERROR_SUCCESS || fileTypeClsid[0] == L'\0')
        {
            wchar_t machineProgIdClsidKey[256] = {};
            hr = StringCchPrintfW(machineProgIdClsidKey, ARRAYSIZE(machineProgIdClsidKey), L"Software\\Classes\\%s\\CLSID", progId);

            if (FAILED(hr))
            {
                return hr;
            }

            fileTypeClsidBytes = sizeof(fileTypeClsid);
            const LSTATUS machineFileTypeClsidStatus = RegGetValueW(
                HKEY_LOCAL_MACHINE,
                machineProgIdClsidKey,
                nullptr,
                RRF_RT_REG_SZ,
                nullptr,
                fileTypeClsid,
                &fileTypeClsidBytes);

            if (machineFileTypeClsidStatus != ERROR_SUCCESS || fileTypeClsid[0] == L'\0')
            {
                return S_OK;
            }
        }

        hr = StringCchPrintfW(
            subKey,
            ARRAYSIZE(subKey),
            L"Software\\Classes\\CLSID\\%s\\ShellEx\\%s",
            fileTypeClsid,
            kThumbnailHandlerGuid);

        return SUCCEEDED(hr) ? DeleteRegistryTree(HKEY_CURRENT_USER, subKey) : hr;
    }

    HRESULT UnregisterApprovedExtension()
    {
        HKEY key = nullptr;
        const LSTATUS openStatus = RegOpenKeyExW(
            HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
            0,
            KEY_SET_VALUE,
            &key);

        if (openStatus == ERROR_FILE_NOT_FOUND)
        {
            return S_OK;
        }

        if (openStatus != ERROR_SUCCESS)
        {
            return HRESULT_FROM_WIN32(openStatus);
        }

        const LSTATUS deleteStatus = RegDeleteValueW(key, kClsidText);
        RegCloseKey(key);
        return deleteStatus == ERROR_SUCCESS || deleteStatus == ERROR_FILE_NOT_FOUND
            ? S_OK
            : HRESULT_FROM_WIN32(deleteStatus);
    }

    class ThumbnailProvider final : public IInitializeWithStream, public IInitializeWithFile, public IThumbnailProvider
    {
    public:
        ThumbnailProvider() : _references(1)
        {
            AddModuleReference();
        }

        ThumbnailProvider(const ThumbnailProvider&) = delete;
        ThumbnailProvider& operator=(const ThumbnailProvider&) = delete;

        ~ThumbnailProvider()
        {
            SafeRelease(&_stream);

            if (_filePath != nullptr)
            {
                CoTaskMemFree(_filePath);
            }

            ReleaseModuleReference();
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;

            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IInitializeWithStream))
            {
                *object = static_cast<IInitializeWithStream*>(this);
            }
            else if (IsEqualIID(riid, IID_IInitializeWithFile))
            {
                *object = static_cast<IInitializeWithFile*>(this);
            }
            else if (IsEqualIID(riid, IID_IThumbnailProvider))
            {
                *object = static_cast<IThumbnailProvider*>(this);
            }
            else
            {
                return E_NOINTERFACE;
            }

            AddRef();
            return S_OK;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_references));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG references = static_cast<ULONG>(InterlockedDecrement(&_references));

            if (references == 0)
            {
                delete this;
            }

            return references;
        }

        IFACEMETHODIMP Initialize(IStream* stream, DWORD mode) override
        {
            UNREFERENCED_PARAMETER(mode);

            if (_stream != nullptr || _filePath != nullptr)
            {
                return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            }

            if (stream == nullptr)
            {
                return E_INVALIDARG;
            }

            _stream = stream;
            _stream->AddRef();
            return S_OK;
        }

        IFACEMETHODIMP Initialize(LPCWSTR filePath, DWORD mode) override
        {
            UNREFERENCED_PARAMETER(mode);

            if (_stream != nullptr || _filePath != nullptr)
            {
                return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            }

            return DuplicateString(filePath, &_filePath);
        }

        IFACEMETHODIMP GetThumbnail(UINT size, HBITMAP* bitmap, WTS_ALPHATYPE* alphaType) override
        {
            if (bitmap == nullptr || alphaType == nullptr)
            {
                return E_POINTER;
            }

            *bitmap = nullptr;
            *alphaType = WTSAT_ARGB;

            wchar_t inputTempPath[MAX_PATH] = {};
            wchar_t outputTempPath[MAX_PATH] = {};
            PCWSTR inputPath = _filePath;
            bool deleteInput = false;

            HRESULT hr = CreateTempPath(outputTempPath, ARRAYSIZE(outputTempPath));

            if (FAILED(hr))
            {
                return hr;
            }

            if (inputPath == nullptr)
            {
                hr = CreateTempPath(inputTempPath, ARRAYSIZE(inputTempPath));

                if (SUCCEEDED(hr))
                {
                    hr = CopyStreamToFile(_stream, inputTempPath);
                }

                if (FAILED(hr))
                {
                    DeleteFileW(outputTempPath);
                    return hr;
                }

                inputPath = inputTempPath;
                deleteInput = true;
            }

            hr = RunThumbnailer(inputPath, outputTempPath, size);

            if (SUCCEEDED(hr))
            {
                hr = LoadPngAsBitmap(outputTempPath, bitmap);
            }

            DeleteFileW(outputTempPath);

            if (deleteInput)
            {
                DeleteFileW(inputTempPath);
            }

            return hr;
        }

    private:
        long _references;
        IStream* _stream = nullptr;
        PWSTR _filePath = nullptr;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() : _references(1)
        {
            AddModuleReference();
        }

        ClassFactory(const ClassFactory&) = delete;
        ClassFactory& operator=(const ClassFactory&) = delete;

        ~ClassFactory()
        {
            ReleaseModuleReference();
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;

            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_references));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG references = static_cast<ULONG>(InterlockedDecrement(&_references));

            if (references == 0)
            {
                delete this;
            }

            return references;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;

            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto provider = new (std::nothrow) ThumbnailProvider();

            if (provider == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            HRESULT hr = provider->QueryInterface(riid, object);
            provider->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                AddModuleReference();
            }
            else
            {
                ReleaseModuleReference();
            }

            return S_OK;
        }

    private:
        long _references;
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);

    if (reason == DLL_PROCESS_ATTACH)
    {
        g_instance = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllCanUnloadNow()
{
    return g_moduleReferences == 0 ? S_OK : S_FALSE;
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllGetClassObject(REFCLSID classId, REFIID riid, void** object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;

    if (!IsEqualCLSID(classId, CLSID_QuickLookerThumbnailProvider))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto factory = new (std::nothrow) ClassFactory();

    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hr = factory->QueryInterface(riid, object);
    factory->Release();
    return hr;
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllRegisterServer()
{
    wchar_t modulePath[MAX_PATH] = {};

    if (GetModuleFileNameW(g_instance, modulePath, ARRAYSIZE(modulePath)) == 0)
    {
        return HResultFromWin32LastError();
    }

    wchar_t clsidKey[256] = {};
    HRESULT hr = StringCchPrintfW(
        clsidKey,
        ARRAYSIZE(clsidKey),
        L"Software\\Classes\\CLSID\\%s",
        kClsidText);

    if (FAILED(hr))
    {
        return hr;
    }

    hr = SetRegistryString(HKEY_CURRENT_USER, clsidKey, nullptr, L"PicPreview Thumbnail Provider");

    if (FAILED(hr))
    {
        return hr;
    }

    wchar_t inprocKey[320] = {};
    hr = StringCchPrintfW(
        inprocKey,
        ARRAYSIZE(inprocKey),
        L"%s\\InprocServer32",
        clsidKey);

    if (FAILED(hr))
    {
        return hr;
    }

    hr = SetRegistryString(HKEY_CURRENT_USER, inprocKey, nullptr, modulePath);

    if (FAILED(hr))
    {
        return hr;
    }

    hr = SetRegistryString(HKEY_CURRENT_USER, inprocKey, L"ThreadingModel", L"Apartment");

    if (FAILED(hr))
    {
        return hr;
    }

    hr = RegisterApprovedExtension();

    if (FAILED(hr))
    {
        return hr;
    }

    const wchar_t* extensions[] = { L".psd", L".psb", L".tga", L".webp", L".avif", L".heic", L".heif" };

    for (PCWSTR extension : extensions)
    {
        hr = RegisterExtension(extension);

        if (FAILED(hr))
        {
            return hr;
        }
    }

    wchar_t thumbnailerPath[MAX_PATH] = {};
    hr = GetSiblingPath(L"QuickLooker.Thumbnailer.exe", thumbnailerPath, ARRAYSIZE(thumbnailerPath));

    if (SUCCEEDED(hr))
    {
        SetRegistryString(HKEY_CURRENT_USER, L"Software\\PicPreview", L"ThumbnailerPath", thumbnailerPath);
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllUnregisterServer()
{
    const wchar_t* extensions[] = { L".psd", L".psb", L".tga", L".webp", L".avif", L".heic", L".heif" };

    for (PCWSTR extension : extensions)
    {
        const HRESULT hr = UnregisterExtension(extension);

        if (FAILED(hr))
        {
            return hr;
        }
    }

    wchar_t clsidKey[256] = {};
    HRESULT hr = StringCchPrintfW(
        clsidKey,
        ARRAYSIZE(clsidKey),
        L"Software\\Classes\\CLSID\\%s",
        kClsidText);

    if (SUCCEEDED(hr))
    {
        hr = DeleteRegistryTree(HKEY_CURRENT_USER, clsidKey);
    }

    const HRESULT approvedHr = UnregisterApprovedExtension();

    if (SUCCEEDED(hr))
    {
        hr = approvedHr;
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return hr;
}
