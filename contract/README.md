# Policy contract

観測・行動・制御周期の定義を置く唯一の場所。学習環境(Unity / 外部バックエンド)と
生成する ROS 2 ノードの両方がこのファイルから生成される。

- `policy_contract.schema.json` : JSON Schema(v0、草案)
- `examples/` : 契約の例

契約の設計方針は `docs/architecture.md` の 2 章を参照。
