#define WIN32_LEAN_AND_MEAN
#define AGY_BUILD
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include "agy_desktop.h"

struct agy_client { wchar_t *helper; wchar_t *name; CRITICAL_SECTION lock; };
static volatile LONG initialized = 0;
typedef struct parser { const unsigned char *at; int depth; } parser;
static void whitespace(parser *p) { while (*p->at && isspace(*p->at)) p->at++; }
static int json_value(parser *p);
static int json_string(parser *p) {
    if (*p->at++ != '"') return 0;
    while (*p->at && *p->at != '"') {
        if (*p->at < 32) return 0;
        if (*p->at++ == '\\') {
            unsigned char escape = *p->at++;
            if (escape == 'u') { for (int n = 0; n < 4; n++) { if (!isxdigit(*p->at)) return 0; p->at++; } }
            else if (!escape || !strchr("\"\\/bfnrt", escape)) return 0;
        }
    }
    return *p->at && *p->at++ == '"';
}
static int json_value(parser *p) {
    whitespace(p);
    if (++p->depth > 40) return 0;
    unsigned char first = *p->at;
    int result = 0;
    if (first == '"') result = json_string(p);
    else if (first == '{' || first == '[') {
        p->at++; whitespace(p);
        unsigned char end = first == '{' ? '}' : ']';
        if (*p->at == end) { p->at++; result = 1; }
        else for (;;) {
            if (first == '{') { if (!json_string(p)) break; whitespace(p); if (*p->at++ != ':') break; }
            if (!json_value(p)) break;
            whitespace(p);
            if (*p->at == end) { p->at++; result = 1; break; }
            if (*p->at++ != ',') break;
            whitespace(p);
        }
    } else if (first == 't' && strncmp((const char *)p->at, "true", 4) == 0) { p->at += 4; result = 1; }
    else if (first == 'f' && strncmp((const char *)p->at, "false", 5) == 0) { p->at += 5; result = 1; }
    else if (first == 'n' && strncmp((const char *)p->at, "null", 4) == 0) { p->at += 4; result = 1; }
    else if (first == '-' || isdigit(first)) {
        if (*p->at == '-') p->at++;
        if (*p->at == '0') p->at++;
        else { if (*p->at < '1' || *p->at > '9') goto done; while (isdigit(*p->at)) p->at++; }
        if (*p->at == '.') { p->at++; if (!isdigit(*p->at)) goto done; while (isdigit(*p->at)) p->at++; }
        if (*p->at == 'e' || *p->at == 'E') { p->at++; if (*p->at == '+' || *p->at == '-') p->at++; if (!isdigit(*p->at)) goto done; while (isdigit(*p->at)) p->at++; }
        result = 1;
    }
done:
    p->depth--; return result;
}
static char *compact_json(const char *json) {
    size_t length = strlen(json);
    if (length > 120000) return NULL;
    parser p = {(const unsigned char *)json, 0}; whitespace(&p);
    if (*p.at != '{' || !json_value(&p)) return NULL;
    whitespace(&p); if (*p.at) return NULL;
    char *copy = (char *)malloc(length + 1);
    if (!copy) return NULL;
    size_t out = 0; int quoted = 0, escaped = 0;
    for (size_t n = 0; n < length; n++) {
        char value = json[n];
        if (!quoted && isspace((unsigned char)value)) continue;
        copy[out++] = value;
        if (quoted && escaped) escaped = 0;
        else if (quoted && value == '\\') escaped = 1;
        else if (value == '"') quoted = !quoted;
    }
    copy[out] = 0; return copy;
}
int agy_init(unsigned int version) { if (version != AGY_ABI_VERSION) return AGY_INVALID_ARGUMENT; InterlockedExchange(&initialized, 1); return AGY_OK; }
int agy_client_create(const wchar_t *helper, const wchar_t *name, agy_client **result) {
    if (result) *result = NULL;
    if (!initialized || !result || !helper || !name || wcslen(helper) > 30000 || wcslen(name) > 120 || !*name || wcschr(helper, L'"')) return AGY_INVALID_ARGUMENT;
    if (!(wcslen(helper) > 3 && ((helper[1] == ':' && helper[2] == '\\') || (helper[0] == '\\' && helper[1] == '\\')))) return AGY_INVALID_ARGUMENT;
    for (const wchar_t *at = name; *at; at++) if (!((*at >= 'a' && *at <= 'z') || (*at >= 'A' && *at <= 'Z') || (*at >= '0' && *at <= '9') || *at == '-')) return AGY_INVALID_ARGUMENT;
    agy_client *client = (agy_client *)calloc(1, sizeof(*client));
    if (!client) return AGY_OUT_OF_MEMORY;
    client->helper = _wcsdup(helper); client->name = _wcsdup(name);
    if (!client->helper || !client->name || !InitializeCriticalSectionEx(&client->lock, 0, 0)) { free(client->helper); free(client->name); free(client); return AGY_OUT_OF_MEMORY; }
    *result = client; return AGY_OK;
}
static int launch(agy_client *client) {
    size_t length = wcslen(client->helper) + wcslen(client->name) + 40;
    wchar_t *command = (wchar_t *)calloc(length, sizeof(wchar_t));
    if (!command) return 0;
    _snwprintf_s(command, length, _TRUNCATE, L"\"%ls\" --daemon --pipe %ls", client->helper, client->name);
    STARTUPINFOW start; PROCESS_INFORMATION process;
    memset(&start, 0, sizeof(start)); memset(&process, 0, sizeof(process)); start.cb = sizeof(start); start.dwFlags = STARTF_USESHOWWINDOW; start.wShowWindow = SW_HIDE;
    BOOL ok = CreateProcessW(client->helper, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &start, &process);
    free(command); if (ok) { CloseHandle(process.hThread); CloseHandle(process.hProcess); } return ok != 0;
}
static int transfer(HANDLE pipe, void *buffer, DWORD size, DWORD *done, int writing, ULONGLONG deadline) {
    OVERLAPPED overlapped; memset(&overlapped, 0, sizeof(overlapped));
    overlapped.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL); if (!overlapped.hEvent) return 0;
    BOOL ok = writing ? WriteFile(pipe, buffer, size, done, &overlapped) : ReadFile(pipe, buffer, size, done, &overlapped);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
        ULONGLONG now = GetTickCount64(); DWORD remaining = now < deadline ? (DWORD)(deadline - now) : 0;
        if (WaitForSingleObject(overlapped.hEvent, remaining) != WAIT_OBJECT_0) {
            CancelIoEx(pipe, &overlapped); GetOverlappedResult(pipe, &overlapped, done, TRUE); ok = FALSE;
        } else ok = GetOverlappedResult(pipe, &overlapped, done, FALSE);
    }
    CloseHandle(overlapped.hEvent); return ok != 0;
}
static TOKEN_USER *process_user(HANDLE process) {
    HANDLE token; DWORD size = 0;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return NULL;
    GetTokenInformation(token, TokenUser, NULL, 0, &size);
    TOKEN_USER *user = (TOKEN_USER *)malloc(size);
    if (!user || !GetTokenInformation(token, TokenUser, user, size, &size)) { free(user); user = NULL; }
    CloseHandle(token); return user;
}
static int same_user_server(HANDLE pipe) {
    ULONG pid = 0; if (!GetNamedPipeServerProcessId(pipe, &pid)) return 0;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid); if (!process) return 0;
    TOKEN_USER *caller = process_user(GetCurrentProcess()), *server = process_user(process);
    int equal = caller && server && EqualSid(caller->User.Sid, server->User.Sid);
    free(caller); free(server); CloseHandle(process); return equal;
}
int agy_call(agy_client *client, const char *method, const char *params, char **response) {
    if (response) *response = NULL;
    if (!client || !response || !method || !params || strlen(method) > 32 || !*method) return AGY_INVALID_ARGUMENT;
    for (const char *at = method; *at; at++) if (!((*at >= 'a' && *at <= 'z') || *at == '_')) return AGY_INVALID_ARGUMENT;
    char *compact = compact_json(params); if (!compact) return AGY_INVALID_ARGUMENT;
    size_t requestSize = strlen(method) + strlen(compact) + 50;
    char *request = (char *)malloc(requestSize); if (!request) { free(compact); return AGY_OUT_OF_MEMORY; }
    int length = snprintf(request, requestSize, "{\"id\":1,\"method\":\"%s\",\"params\":%s}\n", method, compact); free(compact);
    EnterCriticalSection(&client->lock);
    int status = AGY_CONNECT_FAILED, started = 0;
    wchar_t path[160]; _snwprintf_s(path, 160, _TRUNCATE, L"\\\\.\\pipe\\%ls", client->name);
    HANDLE pipe = INVALID_HANDLE_VALUE;
    ULONGLONG connectDeadline = GetTickCount64() + 6000;
    while (GetTickCount64() < connectDeadline) {
        pipe = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
        if (pipe != INVALID_HANDLE_VALUE) break;
        DWORD error = GetLastError();
        if (error == ERROR_PIPE_BUSY) WaitNamedPipeW(path, 100);
        else if (error == ERROR_FILE_NOT_FOUND) { if (!started) { if (!launch(client)) goto cleanup; started = 1; } Sleep(40); }
        else goto cleanup;
    }
    if (pipe == INVALID_HANDLE_VALUE) goto cleanup;
    if (!same_user_server(pipe)) goto cleanup;
    status = AGY_IO_UNKNOWN;
    ULONGLONG deadline = GetTickCount64() + 14500;
    DWORD written = 0; size_t sent = 0;
    while (sent < (size_t)length) { if (!transfer(pipe, request + sent, (DWORD)(length - sent), &written, 1, deadline) || !written) goto cleanup; sent += written; }
    size_t size = 0, capacity = 8192; char *output = (char *)malloc(capacity);
    if (!output) { status = AGY_OUT_OF_MEMORY; goto cleanup; }
    for (;;) {
        if (capacity - size < 4097) {
            if (capacity >= 48 * 1024 * 1024) { free(output); status = AGY_RESPONSE_TOO_LARGE; goto cleanup; }
            capacity *= 2; char *next = (char *)realloc(output, capacity); if (!next) { free(output); status = AGY_OUT_OF_MEMORY; goto cleanup; } output = next;
        }
        DWORD received = 0;
        if (!transfer(pipe, output + size, 4096, &received, 0, deadline) || !received) { free(output); goto cleanup; }
        size += received; output[size] = 0;
        char *end = strchr(output, '\n'); if (end) { *end = 0; *response = output; status = AGY_OK; break; }
    }
cleanup:
    if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
    LeaveCriticalSection(&client->lock); free(request); return status;
}
void agy_free(char *response) { free(response); }
void agy_client_destroy(agy_client *client) { if (!client) return; DeleteCriticalSection(&client->lock); free(client->helper); free(client->name); free(client); }
