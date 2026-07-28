# Legacy Sanity 合成夹具目录

这些文件把当前 v1 存档形状冻结为一个 JSON 对象，key 是严格区分大小写的 `Sanity`：

```json
{ "Sanity": 75 }
```

它们都是开发期合成夹具。测试只读本目录，不读取 `references/` 或真实 Stardew 存档。测试通过只构成 parser seam 的 `LocalVerified` 证据，不证明迁移、SMAPI 生命周期、真实旧档安全或 `GameAccepted`。

| Fixture | 冻结边界 | 预期分类 |
| --- | --- | --- |
| `missing-key.json` | 对象没有 `Sanity` key | `MissingSanityKey` |
| `wrong-case-key.json` | 小写 `sanity` 不是 v1 key | `MissingSanityKey` |
| `zero.json` | 有限值下边界 | `FiniteSanityValue(0)` |
| `half.json` | 代表性中间值 | `FiniteSanityValue(75)` |
| `max.json` | 当前 v1 上限 | `FiniteSanityValue(150)` |
| `negative.json` | 低于当前范围的有限值 | `FiniteSanityValue(-1)` |
| `over-max.json` | 高于当前范围的有限值 | `FiniteSanityValue(151)` |
| `null.json` | 显式 null 字段 | `NullSanityValue` |
| `nan-string.json` | 以字符串保存的 `NaN` | `NonFiniteSanityValue` |
| `positive-infinity-string.json` | 正无穷字符串 | `NonFiniteSanityValue` |
| `negative-infinity-string.json` | 负无穷字符串 | `NonFiniteSanityValue` |
| `non-finite-overflow.json` | JSON 语法合法但超出有限 `double` 范围 | `NonFiniteSanityValue` |
| `bad-field.json` | 普通非数值字符串 | `InvalidSanityType` |
| `invalid-root.json` | 根不是对象 | `InvalidRoot` |
| `bad-json.json` | 截断 JSON | `MalformedJson` |
| `nan-token.json` | 非标准裸 `NaN` token | `MalformedJson` |

seam 刻意原样保留有限负数与超上限值，不提前裁剪。迁移、备份顺序、安全默认值和写保护属于下一编号阶段。
