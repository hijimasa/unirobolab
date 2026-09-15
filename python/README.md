# unirobolab (Python)

契約(`contract/`)を起点に、ポリシーを ROS 2 に載せて検証するためのツール群。
PyPI には公開しない。Unity バイナリが同梱の `uv` でこの git リポジトリを指定して入れる。

```
unirobolab make-test-policy <contract.json> <out.onnx>   # 配線確認用の恒等ポリシーを作る
unirobolab gen <contract.json> --out generated/           # ROS 2 パッケージを生成する
unirobolab sim2sim <contract.json> --pkg <pkg>            # (コンテナ内) 生成物を動かして合否を出す
unirobolab train <contract.json> --config <train.json> --out <run>   # (コンテナ内) PPO で学習し policy.onnx を書く
```

- `gen` は生成パッケージにノード本体 `unirobolab/ros2/policy_node.py` を複製し、契約 JSON と
  ONNX を share に同梱する。ノードは契約を実行時に読むので、生成物にロジックは埋め込まない。
- `sim2sim` は rclpy が要るので ROS 2 環境(コンテナ)で動かす。生成パッケージのノードを起動し、
  目標列を流し、`joint_states` と `policy/status` から合否を判定して JSON レポートを書く。
- `train` は Gymnasium 環境 `ros2/unity_env.py` でシミュレータを一時停止し `step_simulation` で進める。
  観測・行動の処理は `ros2/policy_node.py` の同じ関数を使うので、学習時と配備時の配線は同一。
  出力は `policy.onnx`(方策の平均出力、契約の入出力名)、`progress.csv`(エピソードごとの
  収益・報酬項の寄与・最終誤差)、`learning_curve.png`、`eval.json`。
- ROS 2 環境側の依存(onnxruntime、torch CPU、stable-baselines3 など)は `docker/Dockerfile` で
  サンプルのイメージから派生させて入れる(`scripts/sim2sim_container.sh build`)。
