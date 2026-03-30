CFLAGS += -g -Wall -c -fPIC -O2 -Wno-pointer-sign -Wno-char-subscripts -Wno-unused-variable -Wno-unused-function -Wno-strict-aliasing -Wno-discarded-qualifiers -Wno-unused-const-variable $(CPU_FLAGS)
CXXFLAGS += -g -Wall -fPIC -fpermissive -O2 -Wno-char-subscripts -Wno-unused-variable -Wno-unused-function -Wno-strict-aliasing -Wno-sign-compare -std=c++11 $(CPU_FLAGS)
LDFLAGS += -shared
TARGET = libkawpow.so
TARGET_KERRIGAN = libkerrigankawpow.so

OBJECTS = ethash/ethash.o keccak/keccak.o keccak/keccakf800.o keccak/keccakf1600.o ethash/managed.o ethash/primes.o ethash/progpow.o
KERRIGAN_OBJECTS = ethash/ethash_kerrigan.o keccak/keccak.o keccak/keccakf800.o keccak/keccakf1600.o ethash/managed_kerrigan.o ethash/primes.o ethash/progpow_kerrigan.o

all: $(TARGET) $(TARGET_KERRIGAN)

$(TARGET): $(OBJECTS)
	$(CXX) $(LDFLAGS) -o $@ $^ $(LDLIBS)

$(TARGET_KERRIGAN): $(KERRIGAN_OBJECTS)
	$(CXX) $(LDFLAGS) -o $@ $^ $(LDLIBS)

# Kerrigan-specific object files compiled with -DKERRIGAN_KAWPOW
ethash/ethash_kerrigan.o: ethash/ethash.cpp
	$(CXX) $(CXXFLAGS) -DKERRIGAN_KAWPOW -c -o $@ $<

ethash/managed_kerrigan.o: ethash/managed.cpp
	$(CXX) $(CXXFLAGS) -DKERRIGAN_KAWPOW -c -o $@ $<

ethash/progpow_kerrigan.o: ethash/progpow.cpp
	$(CXX) $(CXXFLAGS) -DKERRIGAN_KAWPOW -c -o $@ $<

.PHONY: clean

clean:
	$(RM) $(TARGET) $(TARGET_KERRIGAN) $(OBJECTS) $(KERRIGAN_OBJECTS)
