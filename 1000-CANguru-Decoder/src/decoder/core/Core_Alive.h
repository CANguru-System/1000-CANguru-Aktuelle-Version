#pragma once
#include <stdint.h>

void aliveInit();
void aliveLoop();

void aliveSetDecoderID(uint8_t id);
uint8_t aliveGetDecoderID();
