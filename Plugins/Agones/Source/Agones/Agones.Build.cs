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

// Agones Unreal 模块构建规则：声明 SDK HTTP/WebSocket 接入所需的引擎模块和包含路径。
// 依赖必须保持最小化并兼容项目目标 Unreal 版本；本文件不承载运行时地址或集群凭据。
using UnrealBuildTool;

public class Agones : ModuleRules
{
	/// <summary>
	/// 为当前 Unreal Target 声明 Agones 插件编译依赖。
	/// 构造过程只修改构建图，不读取集群配置，也不把运行时凭据编译进二进制。
	/// </summary>
	public Agones(ReadOnlyTargetRules target) : base(target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		PublicIncludePaths.AddRange(new string[] {});
		PrivateIncludePaths.AddRange(new string[] {});
		PublicDependencyModuleNames.AddRange(new[]
		{
			"Core",
			"HTTP",
			"Json",
			"JsonUtilities",
			"WebSockets"
		});
		PrivateDependencyModuleNames.AddRange(
			new[]
			{
				"CoreUObject",
				"Engine"
			});
		DynamicallyLoadedModuleNames.AddRange(new string[]{ });
	}
}
