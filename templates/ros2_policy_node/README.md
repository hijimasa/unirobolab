# ros2_policy_node template

ポリシー契約から生成される ROS 2 パッケージの雛形を置く場所。

生成物の中身(予定):

- C++ ノード。ONNX Runtime でポリシーを推論する
- 契約の `observations` 順に観測ベクトルを組み立てる(購読トピック、単位変換、
  スケール、デッドバンド、履歴)
- 契約の `actions` 順に出力を分解し、`ros2_control_interface` に合わせて配信する
- `policy_rate_hz` で周期実行し、実際の周期が契約から外れたら警告する
- launch ファイルと `package.xml` / `CMakeLists.txt`

sim2sim では同じ生成物を Unity シミュレータに接続して合否を出す。
