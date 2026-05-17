# vd-switch

Windows の仮想デスクトップをコマンドラインから直接切り替えるツール。

## 背景・動機

Windows の仮想デスクトップは `Ctrl + Win + 左右キー` で切り替えられますが、**mstsc.exe（リモートデスクトップ接続）** などのアプリにフォーカスがあるとショートカットが奪われて機能しません。Logi Options+ でマウスのサイドボタンに割り当てても、キー入力シミュレーション経由である限り同じ問題が生じます。

根本的な解決には、キーシミュレーションを介さず **仮想デスクトップ API を直接呼び出す** 必要があります。Windows は公式のパブリック API を提供していないため、Shell 内部の COM インターフェースを利用します。

将来的には Stream Deck などの物理デバイスと組み合わせ、どのアプリにフォーカスがあっても確実にデスクトップを切り替えられる環境を構築することを想定しています。

## 仕組み

[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) の COM インターフェース定義（Windows 11 24H2 以降対応版）をソースとして取り込み、外部 DLL への依存なしに仮想デスクトップを操作します。

- 外部 DLL（VirtualDesktopAccessor.dll 等）不要
- `vd-switch.exe` 単体で動作
- .NET 9 self-contained ビルド（ランタイムのインストール不要）

## 動作要件

- Windows 11 24H2 以降

## 使い方

```
vd-switch <番号>      # 指定した番号のデスクトップに切り替え（0始まり）
vd-switch --current   # 現在のデスクトップ番号を表示
vd-switch --count     # デスクトップの総数を表示
vd-switch             # 現在/総数 を表示（例: 1/4）
```

### 例

```
vd-switch 0   # 1枚目のデスクトップへ
vd-switch 2   # 3枚目のデスクトップへ
```

## ビルド方法

.NET 9 SDK が必要です。Linux からのクロスコンパイルも可能です。

```
dotnet publish -c Release
```

成果物は `bin/Release/net9.0-windows/win-x64/publish/vd-switch.exe` に出力されます。

## 注意事項

Windows の仮想デスクトップ内部 COM インターフェースは非公式であり、**Windows Update によって動作しなくなる可能性があります**。Windows のメジャーアップデート後に動作しなくなった場合は、[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) の最新版を確認してください。

## ライセンス

COM インターフェース定義部分は [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop)（MIT License）に基づきます。
