# 整理客户端和 Dedicated Server 的打包地图清单，保证玩法、登录和审查地图按目标隔离。
# 只修改明确的 Packaging 配置；验证每个软引用存在，禁止把编辑器预览地图加入服务器包。
import os
import runpy

scripts_dir = os.path.dirname(os.path.abspath(__file__))
runpy.run_path(os.path.join(scripts_dir, "SanitizeMahjongClientMap.py"), run_name="__main__")
