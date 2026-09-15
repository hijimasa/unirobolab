# unirobolab (Python)

契約(`contract/`)を起点に、ポリシーを ROS 2 に載せて検証するためのツール群。
PyPI には公開しない。Unity バイナリが同梱の `uv` でこの git リポジトリを指定して入れる。

```
unirobolab make-test-policy <contract.json> <out.onnx>   # 配線確認用の恒等ポリシーを作る
unirobolab gen <contract.json> --out generated/           # ROS 2 パッケージを生成する
unirobolab sim2sim <contract.json> --pkg <pkg>            # (コンテナ内) 生成物を動かして合否を出す
unirobolab train <contract.json>                          # 学習バックエンド (未実装)
```

- `gen` は生成パッケージにノード本体 `unirobolab/ros2/policy_node.py` を複製し、契約 JSON と
  ONNX を share に同梱する。ノードは契約を実行時に読むので、生成物にロジックは埋め込まない。
- `sim2sim` は rclpy が要るので ROS 2 環境(コンテナ)で動かす。生成パッケージのノードを起動し、
  目標列を流し、`joint_states` と `policy/status` から合否を判定して JSON レポートを書く。
- ROS 2 環境側には `onnxruntime` が要る(`pip install onnxruntime`)。
