// Copyright Contributors to Agones a Series of LF Projects, LLC.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// Agones 模块实现入口：负责 Unreal 模块加载与卸载，不持有具体 GameInstance 的运行状态。
// 长生命周期连接由 AgonesSubsystem 管理，模块卸载时不得遗留线程、Ticker 或网络回调。
#include "Agones.h"

#define LOCTEXT_NAMESPACE "FAgonesModule"

void FAgonesModule::StartupModule()
{
	// This code will execute after your module is loaded into memory; the exact timing is specified in the .uplugin file per-module
}

void FAgonesModule::ShutdownModule()
{
	// This function may be called during shutdown to clean up your module.  For modules that support dynamic reloading,
	// we call this function before unloading the module.
}

#undef LOCTEXT_NAMESPACE

IMPLEMENT_MODULE(FAgonesModule, Agones)
