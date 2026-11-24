#ifndef AGY_DESKTOP_H
#define AGY_DESKTOP_H
#include <wchar.h>
#if defined(_WIN32) && defined(AGY_BUILD)
#define AGY_API __declspec(dllexport)
#elif defined(_WIN32)
#define AGY_API __declspec(dllimport)
#else
#define AGY_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
#define AGY_ABI_VERSION 1
#define AGY_OK 0
#define AGY_INVALID_ARGUMENT 1
#define AGY_OUT_OF_MEMORY 2
#define AGY_CONNECT_FAILED 3
#define AGY_IO_UNKNOWN 4
#define AGY_RESPONSE_TOO_LARGE 5

typedef struct agy_client agy_client;
AGY_API int agy_init(unsigned int abi_version);
/* helper_exe must be an absolute .exe path; pipe_name is a caller-chosen ASCII name.
   The helper restricts pipe access to the same Windows account. Calls on one client serialize.
   No disk state is created. A helper exits after five idle minutes; its refs then expire. */
AGY_API int agy_client_create(const wchar_t *helper_exe, const wchar_t *pipe_name, agy_client **result);
/* method: windows/observe/find/act/query/system/batch/session/capture/shutdown.
   params_json is a UTF-8 JSON object. Receives an owned UTF-8 JSON response.
   AGY_OK means transport succeeded: inspect response.ok and verification.
   An I/O failure may follow delivered input. Never automatically repeat that input. */
AGY_API int agy_call(agy_client *client, const char *method, const char *params_json, char **response);
AGY_API void agy_free(char *response);
/* Do not destroy a client while another thread is using it. */
AGY_API void agy_client_destroy(agy_client *client);
#ifdef __cplusplus
}
#endif
#endif
