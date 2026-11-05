#include <windows.h>
#include <intrin.h>
#include <stdio.h>
int main() {
    __try {
        RaiseException(0xE0421234u, 0, 0, nullptr);
    } __except (GetExceptionCode() == 0xE0421234u ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        fputs("PROGPU_FIRST_CHANCE_HANDLED_NATIVE\n", stdout);
        fflush(stdout);
    }
    __fastfail(7);
}
