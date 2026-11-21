#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <atomic>
#include <memory>
#include <new>
#include <string>
#include <string_view>
#include <vector>

namespace {

// {8E6570A7-BE80-4E33-803C-589F157E7B71}
const CLSID CommandClass = {0x8e6570a7, 0xbe80, 0x4e33, {0x80, 0x3c, 0x58, 0x9f, 0x15, 0x7e, 0x7b, 0x71}};
std::atomic_ulong LiveObjects = 0;
HMODULE Module;

enum class Action { Root, Zip, SevenZip, Other, ExtractHere, ExtractFolder, ExtractTo, Open };
enum class Selection { Unknown, Files, Archives };

bool EndsWith(std::wstring_view name, std::wstring_view suffix) {
    return name.size() >= suffix.size() &&
        CompareStringOrdinal(name.data() + name.size() - suffix.size(), static_cast<int>(suffix.size()),
            suffix.data(), static_cast<int>(suffix.size()), TRUE) == CSTR_EQUAL;
}

bool IsArchive(std::wstring_view name) {
    constexpr std::wstring_view extensions[] = {
        L".zip", L".7z", L".rar", L".tar", L".tgz", L".txz", L".tbz", L".tbz2", L".tzst",
        L".gz", L".xz", L".bz2", L".zst", L".iso", L".cab", L".wim", L".deb", L".rpm", L".001"
    };
    for (auto extension : extensions) {
        if (EndsWith(name, extension))
            return true;
    }
    return false;
}

std::wstring Executable() {
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(Module, path, ARRAYSIZE(path));
    if (!length || length >= ARRAYSIZE(path))
        return {};
    std::wstring result(path, length);
    result.resize(result.find_last_of(L'\\') + 1);
    return result + L"ZIP.exe";
}

std::wstring ReadName(IShellItemArray* items, DWORD index, SIGDN display = SIGDN_FILESYSPATH) {
    IShellItem* item = nullptr;
    if (!items || FAILED(items->GetItemAt(index, &item)))
        return {};
    PWSTR name = nullptr;
    HRESULT hr = item->GetDisplayName(display, &name);
    item->Release();
    std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> guard(name, CoTaskMemFree);
    return SUCCEEDED(hr) && name ? std::wstring(name) : std::wstring();
}

Selection Describe(IShellItemArray* items) {
    DWORD count = 0;
    if (!items || FAILED(items->GetCount(&count)) || !count)
        return Selection::Unknown;
    // Menu construction only asks the shell for display names and cached attributes.
    for (DWORD index = 0; index < count; ++index) {
        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(index, &item)))
            return Selection::Unknown;
        SFGAOF attrs = 0;
        HRESULT hr = item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_STREAM, &attrs);
        item->Release();
        if (FAILED(hr) || !(attrs & SFGAO_FILESYSTEM))
            return Selection::Unknown;
        if (((attrs & SFGAO_FOLDER) && !(attrs & SFGAO_STREAM)) || !IsArchive(ReadName(items, index)))
            return Selection::Files;
    }
    return Selection::Archives;
}

const wchar_t* Verb(Action action) {
    switch (action) {
        case Action::Zip: return L"zip";
        case Action::SevenZip: return L"7z";
        case Action::Other: return L"other";
        case Action::ExtractHere: return L"here";
        case Action::ExtractFolder: return L"folder";
        case Action::ExtractTo: return L"to";
        case Action::Open: return L"open";
        default: return L"";
    }
}

struct Handle {
    HANDLE value;
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value) CloseHandle(value); }
};

HRESULT Launch(Action action, IShellItemArray* items) {
    DWORD count = 0;
    if (!items || FAILED(items->GetCount(&count)) || !count)
        return E_INVALIDARG;
    std::wstring content(1, L'\xfeff');
    for (DWORD index = 0; index < count; ++index) {
        std::wstring path = ReadName(items, index);
        if (path.empty() || path.find_first_of(L"\r\n") != std::wstring::npos)
            return E_INVALIDARG;
        content += path;
        content += L"\r\n";
    }

    wchar_t temp[32768];
    DWORD length = GetTempPathW(ARRAYSIZE(temp), temp);
    if (!length || length >= ARRAYSIZE(temp))
        return E_FAIL;
    GUID id;
    if (FAILED(CoCreateGuid(&id)))
        return E_FAIL;
    wchar_t guid[40];
    StringFromGUID2(id, guid, ARRAYSIZE(guid));
    std::wstring selection = std::wstring(temp, length) + L"QuickZip-shell-" + guid + L".txt";
    bool written = false;
    {
        Handle file{CreateFileW(selection.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr)};
        if (file.value == INVALID_HANDLE_VALUE)
            return HRESULT_FROM_WIN32(GetLastError());
        DWORD total = static_cast<DWORD>(content.size() * sizeof(wchar_t));
        DWORD bytes = 0;
        written = WriteFile(file.value, content.data(), total, &bytes, nullptr) && bytes == total;
    }
    if (!written) {
        DeleteFileW(selection.c_str());
        return E_FAIL;
    }

    std::wstring exe = Executable();
    std::wstring command = L"\"" + exe + L"\" --shell " + Verb(action) + L" \"" + selection + L"\"";
    POINT position{};
    if (GetCursorPos(&position))
        command += L" " + std::to_wstring(position.x) + L" " + std::to_wstring(position.y);
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process)) {
        DWORD error = GetLastError();
        DeleteFileW(selection.c_str());
        return HRESULT_FROM_WIN32(error);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

class ReferenceCounted {
    std::atomic_ulong references = 1;
protected:
    ReferenceCounted() { ++LiveObjects; }
    virtual ~ReferenceCounted() { --LiveObjects; }
    ULONG Add() { return ++references; }
    ULONG Drop() {
        ULONG count = --references;
        if (!count)
            delete this;
        return count;
    }
};

class Commands;
class Command final : public IExplorerCommand, public ReferenceCounted {
    Action action;
    Selection selection = Selection::Unknown;
public:
    explicit Command(Action value) : action(value) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        *object = nullptr;
        if (iid != IID_IUnknown && iid != IID_IExplorerCommand)
            return E_NOINTERFACE;
        *object = static_cast<IExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return Add(); }
    ULONG STDMETHODCALLTYPE Release() override { return Drop(); }

    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* title) override {
        switch (action) {
            case Action::Root: return SHStrDupW(L"ZIP", title);
            case Action::Zip: return SHStrDupW(L"压缩为 ZIP", title);
            case Action::SevenZip: return SHStrDupW(L"压缩为 7z", title);
            case Action::Other: return SHStrDupW(L"压缩为其他格式", title);
            case Action::ExtractHere: return SHStrDupW(L"解压到此处", title);
            case Action::ExtractFolder: return SHStrDupW(L"解压到同名文件夹", title);
            case Action::ExtractTo: return SHStrDupW(L"解压到其他文件夹…", title);
            case Action::Open: return SHStrDupW(L"用 ZIP 打开", title);
        }
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override {
        *icon = nullptr;
        if (action != Action::Root)
            return E_NOTIMPL;
        try {
            return SHStrDupW((Executable() + L",0").c_str(), icon);
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* name) override {
        *name = CommandClass;
        name->Data1 += static_cast<unsigned int>(action);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        try {
            Selection current = Describe(items);
            if (action == Action::Root && current != Selection::Unknown)
                selection = current;
            if (current == Selection::Unknown) {
                *state = items ? ECS_HIDDEN : ECS_ENABLED;
                return S_OK;
            }
            bool compression = action == Action::Zip || action == Action::SevenZip || action == Action::Other;
            DWORD count = 0;
            items->GetCount(&count);
            bool visible = action == Action::Root ||
                (compression == (current == Selection::Files) && (action != Action::Open || count == 1));
            *state = visible ? ECS_ENABLED : ECS_HIDDEN;
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override {
        if (action == Action::Root)
            return E_NOTIMPL;
        try { return Launch(action, items); }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override {
        *flags = action == Action::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** children) override;
};

class Commands final : public IEnumExplorerCommand, public ReferenceCounted {
    Selection selection;
    ULONG position;
    static constexpr Action Actions[] = {Action::Zip, Action::SevenZip, Action::Other,
        Action::ExtractHere, Action::ExtractFolder, Action::ExtractTo, Action::Open};
    ULONG End() const { return selection == Selection::Files ? 3 : ARRAYSIZE(Actions); }
public:
    explicit Commands(Selection kind, ULONG index = 0)
        : selection(kind), position(index == 0 && kind == Selection::Archives ? 3 : index) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        *object = nullptr;
        if (iid != IID_IUnknown && iid != IID_IEnumExplorerCommand)
            return E_NOINTERFACE;
        *object = static_cast<IEnumExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return Add(); }
    ULONG STDMETHODCALLTYPE Release() override { return Drop(); }
    HRESULT STDMETHODCALLTYPE Next(ULONG requested, IExplorerCommand** commands, ULONG* fetched) override {
        if (!commands || (!fetched && requested != 1))
            return E_POINTER;
        ULONG written = 0;
        if (fetched)
            *fetched = 0;
        while (written < requested && position < End()) {
            auto* command = new(std::nothrow) Command(Actions[position]);
            if (!command) {
                while (written)
                    commands[--written]->Release();
                return E_OUTOFMEMORY;
            }
            commands[written++] = command;
            ++position;
        }
        if (fetched)
            *fetched = written;
        return written == requested ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {
        if (count > End() - position) {
            position = End();
            return S_FALSE;
        }
        position += count;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Reset() override {
        position = selection == Selection::Archives ? 3 : 0;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** copy) override {
        *copy = new(std::nothrow) Commands(selection, position);
        return *copy ? S_OK : E_OUTOFMEMORY;
    }
};

HRESULT Command::EnumSubCommands(IEnumExplorerCommand** children) {
    *children = nullptr;
    if (action != Action::Root)
        return E_NOTIMPL;
    *children = new(std::nothrow) Commands(selection);
    return *children ? S_OK : E_OUTOFMEMORY;
}

class Factory final : public IClassFactory, public ReferenceCounted {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        *object = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory)
            return E_NOINTERFACE;
        *object = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return Add(); }
    ULONG STDMETHODCALLTYPE Release() override { return Drop(); }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) override {
        *object = nullptr;
        if (outer)
            return CLASS_E_NOAGGREGATION;
        auto* value = new(std::nothrow) Command(Action::Root);
        if (!value)
            return E_OUTOFMEMORY;
        HRESULT hr = value->QueryInterface(iid, object);
        value->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock)
            ++LiveObjects;
        else
            --LiveObjects;
        return S_OK;
    }
};

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH)
        Module = instance;
    return TRUE;
}

extern "C" __declspec(dllexport) HRESULT WINAPI DllCanUnloadNow() {
    return LiveObjects == 0 ? S_OK : S_FALSE;
}

extern "C" __declspec(dllexport) HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object) {
    *object = nullptr;
    if (clsid != CommandClass)
        return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new(std::nothrow) Factory();
    if (!factory)
        return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(iid, object);
    factory->Release();
    return hr;
}
