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

// Agones Unreal 模块公共接口：提供模块身份和生命周期声明，供引擎模块系统加载。
// 业务代码应通过 UAgonesSubsystem 使用 SDK，不应依赖模块实现细节或保存全局连接状态。
#pragma once

#include "Modules/ModuleManager.h"

class FAgonesModule : public IModuleInterface
{
public:
	/** IModuleInterface implementation */
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;
};
