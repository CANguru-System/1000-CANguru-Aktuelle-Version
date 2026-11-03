#pragma once
#include <nvs.h>
#include <nvs_flash.h>
#include <esp_err.h>
#include <Arduino.h>

// Hilfe für DecoderBase::factoryReset() der Decodertypen
class DecoderReset
{
public:
    // Löscht den NVS-Namensraum und ruft anschließend loadDefaults() auf.
    // Die Quittung an die Bridge sendet der Kern (Core_Link).
    static bool performReset(const char *nvsNamespace, void (*loadDefaultsCallback)())
    {
        if (!eraseNamespace(nvsNamespace))
            return false;

        if (loadDefaultsCallback)
            loadDefaultsCallback();
        return true;
    }

private:
    static bool eraseNamespace(const char *nvsNamespace)
    {
        nvs_handle handle;
        esp_err_t err = nvs_open(nvsNamespace, NVS_READWRITE, &handle);
        if (err != ESP_OK)
            return false;

        err = nvs_erase_all(handle);
        if (err != ESP_OK)
        {
            nvs_close(handle);
            return false;
        }

        nvs_commit(handle);
        nvs_close(handle);
        return true;
    }
};
