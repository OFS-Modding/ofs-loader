#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include "MinHook.h"

typedef BOOL(WINAPI *GetFileVersionInfoA_Fn)(LPCSTR, DWORD, DWORD, LPVOID);
typedef BOOL(WINAPI *GetFileVersionInfoW_Fn)(LPCWSTR, DWORD, DWORD, LPVOID);
typedef DWORD(WINAPI *GetFileVersionInfoSizeA_Fn)(LPCSTR, LPDWORD);
typedef DWORD(WINAPI *GetFileVersionInfoSizeW_Fn)(LPCWSTR, LPDWORD);
typedef BOOL(WINAPI *VerQueryValueA_Fn)(LPCVOID, LPCSTR, LPVOID *, PUINT);
typedef BOOL(WINAPI *VerQueryValueW_Fn)(LPCVOID, LPCWSTR, LPVOID *, PUINT);

typedef struct VersionApi {
    HMODULE module;
    GetFileVersionInfoA_Fn get_file_version_info_a;
    GetFileVersionInfoW_Fn get_file_version_info_w;
    GetFileVersionInfoSizeA_Fn get_file_version_info_size_a;
    GetFileVersionInfoSizeW_Fn get_file_version_info_size_w;
    VerQueryValueA_Fn ver_query_value_a;
    VerQueryValueW_Fn ver_query_value_w;
    BOOL ready;
} VersionApi;

typedef void *hostfxr_handle;
typedef void(__cdecl *hostfxr_error_writer_fn)(const WCHAR *message);
typedef hostfxr_error_writer_fn(__cdecl *hostfxr_set_error_writer_fn)(
    hostfxr_error_writer_fn error_writer);
typedef struct HostfxrInitializeParameters {
    size_t size;
    const WCHAR *host_path;
    const WCHAR *dotnet_root;
} HostfxrInitializeParameters;
typedef int32_t(__cdecl *hostfxr_initialize_for_dotnet_command_line_fn)(
    int32_t argument_count,
    const WCHAR **arguments,
    const HostfxrInitializeParameters *parameters,
    hostfxr_handle *host_context_handle);
typedef int32_t(__cdecl *hostfxr_get_runtime_delegate_fn)(
    hostfxr_handle host_context_handle,
    int32_t type,
    void **delegate);
typedef int32_t(__cdecl *hostfxr_close_fn)(hostfxr_handle host_context_handle);
typedef int(__stdcall *load_assembly_and_get_function_pointer_fn)(
    const WCHAR *assembly_path,
    const WCHAR *type_name,
    const WCHAR *method_name,
    const WCHAR *delegate_type_name,
    void *reserved,
    void **delegate);
typedef int(__stdcall *component_entry_point_fn)(void *arguments, int32_t argument_size);
typedef void(__cdecl *ofs_main_thread_callback_fn)(void *instance);
typedef BOOL(__cdecl *ofs_install_main_menu_start_hook_fn)(
    void *target,
    ofs_main_thread_callback_fn callback);
typedef BOOL(__cdecl *ofs_button_press_callback_fn)(void *instance);
typedef BOOL(__cdecl *ofs_install_button_press_hook_fn)(
    void *target,
    ofs_button_press_callback_fn callback);
typedef void(__cdecl *ofs_ui_update_callback_fn)(void);
typedef BOOL(__cdecl *ofs_install_ui_update_hook_fn)(
    void *target,
    ofs_ui_update_callback_fn callback);
typedef BOOL(__cdecl *ofs_install_detour_fn)(
    void *target,
    void *replacement,
    void **original);
typedef BOOL(__cdecl *ofs_remove_detour_fn)(void *target);

typedef struct OfsBootstrapApi {
    size_t size;
    ofs_install_main_menu_start_hook_fn install_main_menu_start_hook;
    ofs_install_button_press_hook_fn install_button_press_hook;
    ofs_install_ui_update_hook_fn install_ui_update_hook;
    ofs_install_detour_fn install_detour;
    ofs_remove_detour_fn remove_detour;
} OfsBootstrapApi;

typedef void(__cdecl *main_menu_start_fn)(void *instance, void *method_info);
typedef void(__cdecl *button_press_fn)(void *instance, void *method_info);
typedef void(__cdecl *ui_update_fn)(void *instance, void *method_info);

enum {
    hdt_load_assembly_and_get_function_pointer = 5
};

static HMODULE g_self_module;
static INIT_ONCE g_version_init_once = INIT_ONCE_STATIC_INIT;
static VersionApi g_version_api;
static main_menu_start_fn g_original_main_menu_start;
static ofs_main_thread_callback_fn g_main_thread_callback;
static button_press_fn g_original_button_press;
static ofs_button_press_callback_fn g_button_press_callback;
static ui_update_fn g_original_ui_update;
static ofs_ui_update_callback_fn g_ui_update_callback;
static SRWLOCK g_detour_lock = SRWLOCK_INIT;

void *memcpy(void *destination, const void *source, size_t count) {
    uint8_t *destination_bytes = (uint8_t *)destination;
    const uint8_t *source_bytes = (const uint8_t *)source;
    for (size_t index = 0; index < count; ++index) {
        destination_bytes[index] = source_bytes[index];
    }
    return destination;
}

void *memset(void *destination, int value, size_t count) {
    uint8_t *destination_bytes = (uint8_t *)destination;
    for (size_t index = 0; index < count; ++index) {
        destination_bytes[index] = (uint8_t)value;
    }
    return destination;
}

static BOOL append_path(WCHAR *buffer, DWORD capacity, const WCHAR *suffix) {
    DWORD length = lstrlenW(buffer);
    DWORD suffix_length = lstrlenW(suffix);
    if (length + suffix_length + 1 > capacity) {
        return FALSE;
    }

    CopyMemory(buffer + length, suffix, (suffix_length + 1) * sizeof(WCHAR));
    return TRUE;
}

static BOOL get_game_directory(WCHAR *buffer, DWORD capacity) {
    DWORD length = GetModuleFileNameW(g_self_module, buffer, capacity);
    if (length == 0 || length >= capacity) {
        return FALSE;
    }

    while (length > 0) {
        --length;
        if (buffer[length] == L'\\' || buffer[length] == L'/') {
            buffer[length] = L'\0';
            return TRUE;
        }
    }

    return FALSE;
}

static BOOL is_game_process(void) {
    WCHAR process_path[MAX_PATH];
    DWORD length = GetModuleFileNameW(NULL, process_path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) {
        return FALSE;
    }

    const WCHAR *file_name = process_path;
    for (DWORD index = 0; index < length; ++index) {
        if (process_path[index] == L'\\' || process_path[index] == L'/') {
            file_name = process_path + index + 1;
        }
    }

    return lstrcmpiW(file_name, L"Ore Factory Squad.exe") == 0;
}

static void write_bootstrap_log(const char *message) {
    WCHAR path[MAX_PATH];
    if (!get_game_directory(path, MAX_PATH)) {
        return;
    }

    if (!append_path(path, MAX_PATH, L"\\OFS")) {
        return;
    }
    CreateDirectoryW(path, NULL);

    if (!append_path(path, MAX_PATH, L"\\logs")) {
        return;
    }
    CreateDirectoryW(path, NULL);

    if (!append_path(path, MAX_PATH, L"\\bootstrap.log")) {
        return;
    }

    HANDLE file = CreateFileW(
        path,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    char line[1024];
    const char *prefix = "[OFS Bootstrap] ";
    int prefix_length = lstrlenA(prefix);
    int message_length = lstrlenA(message);
    int capacity = (int)sizeof(line) - prefix_length - 3;
    if (message_length > capacity) {
        message_length = capacity;
    }

    CopyMemory(line, prefix, (SIZE_T)prefix_length);
    CopyMemory(line + prefix_length, message, (SIZE_T)message_length);
    line[prefix_length + message_length] = '\r';
    line[prefix_length + message_length + 1] = '\n';

    DWORD ignored;
    WriteFile(file, line, (DWORD)(prefix_length + message_length + 2), &ignored, NULL);
    CloseHandle(file);
}

static void write_bootstrap_log_wide(const WCHAR *message) {
    char converted[900];
    int index = 0;
    while (message[index] != L'\0' && index < (int)sizeof(converted) - 1) {
        WCHAR character = message[index];
        converted[index] = character <= 0x7f ? (char)character : '?';
        ++index;
    }
    converted[index] = '\0';
    write_bootstrap_log(converted);
}

static void __cdecl hostfxr_error_writer(const WCHAR *message) {
    write_bootstrap_log_wide(message);
}

static BOOL is_executable_address(void *address) {
    MEMORY_BASIC_INFORMATION memory;
    if (address == NULL || VirtualQuery(address, &memory, sizeof(memory)) == 0) {
        return FALSE;
    }

    DWORD protection = memory.Protect & 0xff;
    BOOL executable =
        protection == PAGE_EXECUTE ||
        protection == PAGE_EXECUTE_READ ||
        protection == PAGE_EXECUTE_READWRITE ||
        protection == PAGE_EXECUTE_WRITECOPY;
    return memory.State == MEM_COMMIT && executable &&
        (memory.Protect & PAGE_GUARD) == 0;
}

static void __cdecl main_menu_start_detour(void *instance, void *method_info) {
    g_original_main_menu_start(instance, method_info);
    if (g_main_thread_callback != NULL) {
        g_main_thread_callback(instance);
    }
}

static BOOL __cdecl install_main_menu_start_hook(
    void *target,
    ofs_main_thread_callback_fn callback) {
    if (!is_executable_address(target) || callback == NULL) {
        write_bootstrap_log("Rejected an invalid MainMenuManager.Start hook request.");
        return FALSE;
    }

    if (g_original_main_menu_start != NULL) {
        write_bootstrap_log("MainMenuManager.Start hook was already installed.");
        return FALSE;
    }

    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        write_bootstrap_log("MinHook initialization failed.");
        return FALSE;
    }

    g_main_thread_callback = callback;
    status = MH_CreateHook(
        target,
        (LPVOID)main_menu_start_detour,
        (LPVOID *)&g_original_main_menu_start);
    if (status != MH_OK) {
        g_main_thread_callback = NULL;
        g_original_main_menu_start = NULL;
        write_bootstrap_log("Creating the MainMenuManager.Start hook failed.");
        return FALSE;
    }

    status = MH_EnableHook(target);
    if (status != MH_OK) {
        MH_RemoveHook(target);
        g_main_thread_callback = NULL;
        g_original_main_menu_start = NULL;
        write_bootstrap_log("Enabling the MainMenuManager.Start hook failed.");
        return FALSE;
    }

    write_bootstrap_log("MainMenuManager.Start hook installed.");
    return TRUE;
}

static void __cdecl button_press_detour(void *instance, void *method_info) {
    if (g_button_press_callback != NULL && g_button_press_callback(instance)) {
        return;
    }
    g_original_button_press(instance, method_info);
}

static BOOL __cdecl install_button_press_hook(
    void *target,
    ofs_button_press_callback_fn callback) {
    if (!is_executable_address(target) || callback == NULL) {
        write_bootstrap_log("Rejected an invalid Button.Press hook request.");
        return FALSE;
    }

    if (g_original_button_press != NULL) {
        write_bootstrap_log("Button.Press hook was already installed.");
        return FALSE;
    }

    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        write_bootstrap_log("MinHook initialization failed for Button.Press.");
        return FALSE;
    }

    g_button_press_callback = callback;
    status = MH_CreateHook(
        target,
        (LPVOID)button_press_detour,
        (LPVOID *)&g_original_button_press);
    if (status != MH_OK) {
        g_button_press_callback = NULL;
        g_original_button_press = NULL;
        write_bootstrap_log("Creating the Button.Press hook failed.");
        return FALSE;
    }

    status = MH_EnableHook(target);
    if (status != MH_OK) {
        MH_RemoveHook(target);
        g_button_press_callback = NULL;
        g_original_button_press = NULL;
        write_bootstrap_log("Enabling the Button.Press hook failed.");
        return FALSE;
    }

    write_bootstrap_log("Button.Press hook installed.");
    return TRUE;
}

static void __cdecl ui_update_detour(void *instance, void *method_info) {
    g_original_ui_update(instance, method_info);
    if (g_ui_update_callback != NULL) {
        g_ui_update_callback();
    }
}

static BOOL __cdecl install_ui_update_hook(
    void *target,
    ofs_ui_update_callback_fn callback) {
    if (!is_executable_address(target) || callback == NULL) {
        write_bootstrap_log("Rejected an invalid EventSystem.Update hook request.");
        return FALSE;
    }

    if (g_original_ui_update != NULL) {
        write_bootstrap_log("EventSystem.Update hook was already installed.");
        return FALSE;
    }

    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        write_bootstrap_log("MinHook initialization failed for EventSystem.Update.");
        return FALSE;
    }

    g_ui_update_callback = callback;
    status = MH_CreateHook(
        target,
        (LPVOID)ui_update_detour,
        (LPVOID *)&g_original_ui_update);
    if (status != MH_OK) {
        g_ui_update_callback = NULL;
        g_original_ui_update = NULL;
        write_bootstrap_log("Creating the EventSystem.Update hook failed.");
        return FALSE;
    }

    status = MH_EnableHook(target);
    if (status != MH_OK) {
        MH_RemoveHook(target);
        g_ui_update_callback = NULL;
        g_original_ui_update = NULL;
        write_bootstrap_log("Enabling the EventSystem.Update hook failed.");
        return FALSE;
    }

    write_bootstrap_log("EventSystem.Update hook installed.");
    return TRUE;
}

static BOOL __cdecl install_detour(
    void *target,
    void *replacement,
    void **original) {
    if (!is_executable_address(target) ||
        !is_executable_address(replacement) ||
        original == NULL) {
        write_bootstrap_log("Rejected an invalid mod detour request.");
        return FALSE;
    }

    *original = NULL;
    AcquireSRWLockExclusive(&g_detour_lock);
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        ReleaseSRWLockExclusive(&g_detour_lock);
        write_bootstrap_log("MinHook initialization failed for a mod detour.");
        return FALSE;
    }

    status = MH_CreateHook(target, replacement, original);
    if (status != MH_OK) {
        *original = NULL;
        ReleaseSRWLockExclusive(&g_detour_lock);
        write_bootstrap_log("Creating a mod detour failed.");
        return FALSE;
    }

    status = MH_EnableHook(target);
    if (status != MH_OK) {
        MH_RemoveHook(target);
        *original = NULL;
        ReleaseSRWLockExclusive(&g_detour_lock);
        write_bootstrap_log("Enabling a mod detour failed.");
        return FALSE;
    }

    ReleaseSRWLockExclusive(&g_detour_lock);
    write_bootstrap_log("Mod detour installed.");
    return TRUE;
}

static BOOL __cdecl remove_detour(void *target) {
    if (!is_executable_address(target)) {
        write_bootstrap_log("Rejected an invalid mod detour removal request.");
        return FALSE;
    }

    AcquireSRWLockExclusive(&g_detour_lock);
    MH_STATUS status = MH_DisableHook(target);
    if (status != MH_OK) {
        ReleaseSRWLockExclusive(&g_detour_lock);
        write_bootstrap_log("Disabling a mod detour failed.");
        return FALSE;
    }

    status = MH_RemoveHook(target);
    ReleaseSRWLockExclusive(&g_detour_lock);
    if (status != MH_OK) {
        write_bootstrap_log("Removing a mod detour failed.");
        return FALSE;
    }

    write_bootstrap_log("Mod detour removed.");
    return TRUE;
}

static const OfsBootstrapApi g_bootstrap_api = {
    sizeof(OfsBootstrapApi),
    install_main_menu_start_hook,
    install_button_press_hook,
    install_ui_update_hook,
    install_detour,
    remove_detour
};

static FARPROC require_export(HMODULE module, const char *name) {
    FARPROC function = GetProcAddress(module, name);
    if (function == NULL) {
        write_bootstrap_log("Failed to resolve an export from the system version.dll.");
    }
    return function;
}

static BOOL CALLBACK initialize_version_api(PINIT_ONCE once, PVOID parameter, PVOID *context) {
    (void)once;
    (void)parameter;
    (void)context;

    WCHAR system_path[MAX_PATH];
    UINT length = GetSystemDirectoryW(system_path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH || !append_path(system_path, MAX_PATH, L"\\version.dll")) {
        write_bootstrap_log("Failed to build the system version.dll path.");
        return TRUE;
    }

    g_version_api.module = LoadLibraryW(system_path);
    if (g_version_api.module == NULL) {
        write_bootstrap_log("Failed to load the system version.dll.");
        return TRUE;
    }

    g_version_api.get_file_version_info_a =
        (GetFileVersionInfoA_Fn)require_export(g_version_api.module, "GetFileVersionInfoA");
    g_version_api.get_file_version_info_w =
        (GetFileVersionInfoW_Fn)require_export(g_version_api.module, "GetFileVersionInfoW");
    g_version_api.get_file_version_info_size_a =
        (GetFileVersionInfoSizeA_Fn)require_export(g_version_api.module, "GetFileVersionInfoSizeA");
    g_version_api.get_file_version_info_size_w =
        (GetFileVersionInfoSizeW_Fn)require_export(g_version_api.module, "GetFileVersionInfoSizeW");
    g_version_api.ver_query_value_a =
        (VerQueryValueA_Fn)require_export(g_version_api.module, "VerQueryValueA");
    g_version_api.ver_query_value_w =
        (VerQueryValueW_Fn)require_export(g_version_api.module, "VerQueryValueW");

    g_version_api.ready =
        g_version_api.get_file_version_info_a != NULL &&
        g_version_api.get_file_version_info_w != NULL &&
        g_version_api.get_file_version_info_size_a != NULL &&
        g_version_api.get_file_version_info_size_w != NULL &&
        g_version_api.ver_query_value_a != NULL &&
        g_version_api.ver_query_value_w != NULL;

    write_bootstrap_log(g_version_api.ready
        ? "System version.dll forwarding initialized."
        : "System version.dll forwarding is incomplete.");
    return TRUE;
}

static BOOL ensure_version_api(void) {
    InitOnceExecuteOnce(&g_version_init_once, initialize_version_api, NULL, NULL);
    return g_version_api.ready;
}

static BOOL initialize_coreclr(void) {
    WCHAR runtime_directory[MAX_PATH];
    if (!get_game_directory(runtime_directory, MAX_PATH) ||
        !append_path(runtime_directory, MAX_PATH, L"\\OFS\\runtime")) {
        write_bootstrap_log("Failed to build the OFS runtime directory path.");
        return FALSE;
    }

    WCHAR hostfxr_path[MAX_PATH];
    WCHAR assembly_path[MAX_PATH];
    lstrcpyW(hostfxr_path, runtime_directory);
    lstrcpyW(assembly_path, runtime_directory);

    if (!append_path(hostfxr_path, MAX_PATH, L"\\hostfxr.dll") ||
        !append_path(assembly_path, MAX_PATH, L"\\OFS.Runtime.Entry.dll")) {
        write_bootstrap_log("An OFS runtime path exceeded MAX_PATH.");
        return FALSE;
    }

    HMODULE hostfxr = LoadLibraryW(hostfxr_path);
    if (hostfxr == NULL) {
        write_bootstrap_log("Failed to load OFS/runtime/hostfxr.dll.");
        return FALSE;
    }

    hostfxr_initialize_for_dotnet_command_line_fn initialize =
        (hostfxr_initialize_for_dotnet_command_line_fn)GetProcAddress(
            hostfxr,
            "hostfxr_initialize_for_dotnet_command_line");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close = (hostfxr_close_fn)GetProcAddress(hostfxr, "hostfxr_close");
    hostfxr_set_error_writer_fn set_error_writer =
        (hostfxr_set_error_writer_fn)GetProcAddress(hostfxr, "hostfxr_set_error_writer");
    if (initialize == NULL || get_runtime_delegate == NULL || close == NULL ||
        set_error_writer == NULL) {
        write_bootstrap_log("hostfxr is missing required hosting exports.");
        return FALSE;
    }

    set_error_writer(hostfxr_error_writer);
    HostfxrInitializeParameters parameters;
    parameters.size = sizeof(parameters);
    parameters.host_path = assembly_path;
    parameters.dotnet_root = runtime_directory;

    hostfxr_handle context = NULL;
    const WCHAR *arguments[] = {assembly_path};
    int32_t result = initialize(1, arguments, &parameters, &context);
    if (result < 0 || context == NULL) {
        write_bootstrap_log("hostfxr_initialize_for_dotnet_command_line failed.");
        set_error_writer(NULL);
        return FALSE;
    }

    load_assembly_and_get_function_pointer_fn load_assembly = NULL;
    result = get_runtime_delegate(
        context,
        hdt_load_assembly_and_get_function_pointer,
        (void **)&load_assembly);
    if (result < 0 || load_assembly == NULL) {
        close(context);
        write_bootstrap_log("hostfxr_get_runtime_delegate failed.");
        set_error_writer(NULL);
        return FALSE;
    }

    component_entry_point_fn entry_point = NULL;
    result = load_assembly(
        assembly_path,
        L"OFS.Runtime.Entry.BootstrapEntry, OFS.Runtime.Entry",
        L"Initialize",
        (const WCHAR *)(intptr_t)-1,
        NULL,
        (void **)&entry_point);
    close(context);
    if (result < 0 || entry_point == NULL) {
        write_bootstrap_log("Failed to resolve the OFS managed entry point.");
        set_error_writer(NULL);
        return FALSE;
    }

    write_bootstrap_log("CoreCLR initialized; invoking OFS.Runtime.Entry.");
    result = entry_point((void *)&g_bootstrap_api, (int32_t)sizeof(g_bootstrap_api));
    write_bootstrap_log(result == 0
        ? "OFS.Runtime.Entry completed successfully."
        : "OFS.Runtime.Entry reported an initialization failure.");
    set_error_writer(NULL);
    return result == 0;
}

static BOOL wait_for_game_assembly(void) {
    for (int attempt = 0; attempt < 600; ++attempt) {
        if (GetModuleHandleW(L"GameAssembly.dll") != NULL) {
            return TRUE;
        }
        Sleep(50);
    }

    write_bootstrap_log("GameAssembly.dll was not loaded after 30 seconds.");
    return FALSE;
}

static DWORD WINAPI bootstrap_worker(LPVOID parameter) {
    (void)parameter;
    if (!is_game_process()) {
        return 0;
    }

    write_bootstrap_log("OFS Bootstrap loaded; waiting for GameAssembly.dll.");
    if (!wait_for_game_assembly()) {
        return 0;
    }
    write_bootstrap_log("GameAssembly.dll loaded; starting CoreCLR host.");
    initialize_coreclr();
    return 0;
}

BOOL WINAPI GetFileVersionInfoA(
    LPCSTR filename,
    DWORD handle,
    DWORD length,
    LPVOID data) {
    return ensure_version_api()
        ? g_version_api.get_file_version_info_a(filename, handle, length, data)
        : FALSE;
}

BOOL WINAPI GetFileVersionInfoW(
    LPCWSTR filename,
    DWORD handle,
    DWORD length,
    LPVOID data) {
    return ensure_version_api()
        ? g_version_api.get_file_version_info_w(filename, handle, length, data)
        : FALSE;
}

DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR filename, LPDWORD handle) {
    return ensure_version_api()
        ? g_version_api.get_file_version_info_size_a(filename, handle)
        : 0;
}

DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR filename, LPDWORD handle) {
    return ensure_version_api()
        ? g_version_api.get_file_version_info_size_w(filename, handle)
        : 0;
}

BOOL WINAPI VerQueryValueA(
    LPCVOID block,
    LPCSTR sub_block,
    LPVOID *buffer,
    PUINT length) {
    return ensure_version_api()
        ? g_version_api.ver_query_value_a(block, sub_block, buffer, length)
        : FALSE;
}

BOOL WINAPI VerQueryValueW(
    LPCVOID block,
    LPCWSTR sub_block,
    LPVOID *buffer,
    PUINT length) {
    return ensure_version_api()
        ? g_version_api.ver_query_value_w(block, sub_block, buffer, length)
        : FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        g_self_module = instance;
        DisableThreadLibraryCalls(instance);

        HANDLE thread = CreateThread(NULL, 0, bootstrap_worker, NULL, 0, NULL);
        if (thread != NULL) {
            CloseHandle(thread);
        }
    } else if (reason == DLL_PROCESS_DETACH && g_version_api.module != NULL) {
        FreeLibrary(g_version_api.module);
    }

    return TRUE;
}

BOOL WINAPI _DllMainCRTStartup(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    return DllMain(instance, reason, reserved);
}
