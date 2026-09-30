#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_MODULE_H

#include <iostream>
#include <stdexcept>

namespace {

// Algorithm: fixed-work public-API capability checks on two independent libraries.
// Time complexity: O(1), without font parsing, shaping, rasterization or GPU work.
// Space complexity: two scoped library instances and O(1) probe state.
class library_owner final {
public:
    library_owner()
    {
        if (FT_Init_FreeType(&value_) != 0) {
            throw std::runtime_error("FreeType initialization failed");
        }
    }

    ~library_owner()
    {
        if (value_ != nullptr) {
            FT_Done_FreeType(value_);
        }
    }

    library_owner(const library_owner&) = delete;
    library_owner& operator=(const library_owner&) = delete;

    FT_Library get() const noexcept { return value_; }

private:
    FT_Library value_ = nullptr;
};

unsigned int interpreter(FT_Library library)
{
    unsigned int value = 0;
    if (FT_Property_Get(library, "truetype", "interpreter-version", &value) != 0) {
        throw std::runtime_error("TrueType interpreter capability is absent");
    }
    return value;
}

void set_interpreter(FT_Library library, unsigned int value)
{
    if (FT_Property_Set(library, "truetype", "interpreter-version", &value) != 0 ||
        interpreter(library) != value) {
        throw std::runtime_error("Requested TrueType interpreter is unavailable");
    }
}

void verify_version(FT_Library library)
{
    FT_Int major = 0;
    FT_Int minor = 0;
    FT_Int patch = 0;
    FT_Library_Version(library, &major, &minor, &patch);
    if (major != 2 || minor != 14 || patch != 3 ||
        FREETYPE_MAJOR != major || FREETYPE_MINOR != minor || FREETYPE_PATCH != patch) {
        throw std::runtime_error("Prepared header/runtime version identity differs from the pin");
    }
}

} // namespace

int main()
{
    try {
        library_owner first;
        library_owner second;
        verify_version(first.get());
        verify_version(second.get());
        set_interpreter(first.get(), 35);
        set_interpreter(second.get(), 40);
        if (interpreter(first.get()) != 35) {
            throw std::runtime_error("TrueType policy escaped its owning library");
        }
        unsigned int invalid = 0xFFFFFFFFU;
        if (FT_Property_Set(first.get(), "truetype", "interpreter-version", &invalid) == 0 ||
            interpreter(first.get()) != 35 || interpreter(second.get()) != 40) {
            throw std::runtime_error("Invalid TrueType policy changed a live library");
        }
        set_interpreter(first.get(), 40);
        if (interpreter(second.get()) != 40) {
            throw std::runtime_error("Independent TrueType policy changed");
        }
        std::cout << "{\"version\":\"2.14.3\",\"interpreters\":[35,40],"
                     "\"independentPolicies\":true,\"invalidPolicyRejected\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
