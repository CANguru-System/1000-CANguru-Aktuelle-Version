#include "protocol_constants_core.h"
#include "DecoderBase.h"
#include "DecoderFactory.h"

#if DECODER_TYPE == DEVTYPE_SIGNAL || DECODER_TYPE == DEVTYPE_SWITCH
#include "../features/accessory/StepperAccessory.h"
#elif DECODER_TYPE == DEVTYPE_LEDSIGNAL
#include "../features/accessory/LedSignalAccessory.h"
#elif DECODER_TYPE == DEVTYPE_POWER
#include "../features/power/PowerDecoder.h"
#elif DECODER_TYPE == DEVTYPE_RM
#include "../features/feedback/FeedbackDecoder.h"
#endif

DecoderBase* createDecoder() {
#if DECODER_TYPE == DEVTYPE_SIGNAL || DECODER_TYPE == DEVTYPE_SWITCH
    // Weiche und Formsignal: Zubehör-Decoder mit Stepper-Antrieb (ein Quelltext)
    return new StepperAccessory();
#elif DECODER_TYPE == DEVTYPE_LEDSIGNAL
    // vier LED-Lichtsignale rot/grün (klassischer ESP32)
    return new LedSignalAccessory();
#elif DECODER_TYPE == DEVTYPE_POWER
    return new PowerDecoder();
#elif DECODER_TYPE == DEVTYPE_RM
    return new FeedbackDecoder();
#else
    return nullptr;
#endif
}
