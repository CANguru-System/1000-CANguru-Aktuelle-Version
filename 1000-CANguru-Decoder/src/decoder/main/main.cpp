#include <Arduino.h>
#include "soc/rtc_cntl_reg.h"
#include "../core/DecoderBase.h"
#include "DecoderFactory.h"
#include "../core/Core_EspNow.h"
#include "../core/Core_Config.h"
#include "protocol_constants_core.h"
#include "../core/Core_Alive.h"

//extern DecoderBase* gDecoder;

// Kennung der Firmware im Programmabbild. Die CANguru-GUI sucht diesen Text in der
// .bin-Datei und warnt, wenn sie für einen anderen Decodertyp gebaut ist als der Decoder,
// der das Update bekommen soll (Beispiel: switch.bin für ein Formsignal).
#if DECODER_TYPE == DEVTYPE_SWITCH
#define CG_FW_TYPE_NAME "switch"
#elif DECODER_TYPE == DEVTYPE_SIGNAL
#define CG_FW_TYPE_NAME "signal"
#elif DECODER_TYPE == DEVTYPE_LEDSIGNAL
#define CG_FW_TYPE_NAME "ledsignal"
#elif DECODER_TYPE == DEVTYPE_POWER
#define CG_FW_TYPE_NAME "power"
#elif DECODER_TYPE == DEVTYPE_RM
#define CG_FW_TYPE_NAME "feedback"
#elif DECODER_TYPE == DEVTYPE_LIGHT
#define CG_FW_TYPE_NAME "light"
#else
#define CG_FW_TYPE_NAME "unknown"
#endif
extern "C" const char CG_FIRMWARE_ID[] __attribute__((used)) = "CANGURU-FW:" CG_FW_TYPE_NAME ";";

// Decoder/Slaves registieren nur die Adresse des Masters, von dem sie den Broadcast empfangen haben. Alle Antworten gehen an diese Adresse zurück.
// Es können also nur Slaves mit einem Master kommunizieren, nicht aber untereinander.
// Die Broadcast-Adresse wird beim ersten Empfang registriert.
/*void setup() {
    pinMode(GPIO_NUM_8, OUTPUT);
}

void loop() {
    digitalWrite(GPIO_NUM_8, HIGH);
    delay(500);
    digitalWrite(GPIO_NUM_8, LOW);
    delay(500);
}
*/
void setup() {
    WRITE_PERI_REG(RTC_CNTL_BROWN_OUT_REG, 0); // disable brownout detector
    // Kennung benutzen, damit der Linker sie nicht als unbenutzt entfernt
    // (die Adresse wird an einen leeren Assembler-Block übergeben)
    asm volatile("" : : "r"(CG_FIRMWARE_ID) : "memory");
/*    Serial.begin(bdrMonitor);
    delay(350);
    Serial.println("DECODER: setup()");

    coreEspNowInit();*/          // ESP-NOW + Bridge-Peer + MAC
    aliveInit();
    gDecoder = createDecoder();
    if (gDecoder) {
        gDecoder->setup();
    }
}

void loop() {
    if (gDecoder)
        gDecoder->loop();
     aliveLoop();            // Alive separat
}
