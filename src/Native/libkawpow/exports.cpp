/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#include "ethash/ethash.h"
#include "ethash/progpow.hpp"
#include <cstring>

extern "C" __attribute__((visibility("default")))
ethash_result hash(const ethash_epoch_context* context, int block_number,
                   const ethash_hash256* header_hash, uint64_t nonce)
{
    auto result = progpow::hash(*context, block_number,
        *reinterpret_cast<const ethash::hash256*>(header_hash), nonce);
    ethash_result ret;
    std::memcpy(&ret, &result, sizeof(ret));
    return ret;
}

extern "C" __attribute__((visibility("default")))
ethash_hash256 calculate_epoch_seed(int epoch_number)
{
    return ethash_calculate_epoch_seed(epoch_number);
}
