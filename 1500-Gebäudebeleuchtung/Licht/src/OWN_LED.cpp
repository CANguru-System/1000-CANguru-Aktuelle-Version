#include "OWN_LED.h"

static uint8_t ledPin;

void LED_begin(uint8_t pin)
{
  ledPin = pin;
  pinMode(ledPin, OUTPUT);
  digitalWrite(ledPin, LOW); // off
}

void LED_on()
{
  digitalWrite(ledPin, HIGH);
}

void LED_off()
{
  digitalWrite(ledPin, LOW);
}
