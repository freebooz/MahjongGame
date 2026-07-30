# 只读输出目标 Unreal 枚举名称和值，用于确认 Python 自动化脚本与当前引擎 API 兼容。
# 不修改资产或配置；探测结果只用于诊断，不得硬编码本机专属枚举顺序。
import unreal

for enum_name in ("TextureCompressionSettings", "TextureGroup", "TextureMipGenSettings", "SlateBrushDrawType", "MaterialDomain"):
    enum_type = getattr(unreal, enum_name)
    unreal.log(f"[EnumProbe] {enum_name}: {', '.join(name for name in dir(enum_type) if name.isupper())}")
