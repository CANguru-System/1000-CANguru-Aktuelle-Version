// On-board LED of the ESP32-C3 (formerly from the CANguru library)
#ifndef OWN_LED
#define OWN_LED

#include <Arduino.h>

void LED_begin(uint8_t pin);
void LED_on();
void LED_off();

#endif
