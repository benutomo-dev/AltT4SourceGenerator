# SourceGeneratorT4

SourceGeneratorT4はT4っぽい構文でソース生成を行うことができるソースジェネレータです。

## 使い方

### パッケージ参照

プロジェクトの`.csproj`に以下の`PackageReference`を追加します。

```xml
<PackageReference Include="Benutomo.SourceGeneratorT4" Version="1.0.0-alpha2" PrivateAssets="true" />
```

### 基本的な使用方法

拡張子が`.sgtt`のファイルがSourceGeneratorT4のテキストテンプレートファイルとして扱われます。
プロジェクトフォルダに含まれる`.sgtt`は自動的にAdditionalFilesに取り込まれます。`.csproj`に明示的な記載は不要です。

基本的な構文はT4を踏襲しています。コードの断片は`<# code #>`で埋め込むことが可能です。また、式の評価結果も`<#= expression #>`で埋め込み可能です。

例えば、以下のファイルを`Sample.sgtt`として配置しすると

```
<#
var types = new [] {
    "char",
    "int",
    "double",
};
#>

namespace TemplateSamples {
    class Sample {
        <# foreach (var t in types) { #>
        public <#= t #> Default => default(<#= t #>);
        <# } #>
    }
}
```

ソースジェネレータによって以下のファイルが生成され、`Sample.sgtt`を含むプロジェクトのコンパイル対象に追加されます。

```
namespace TemplateSamples {
    class Sample {
        
        public char Default => default(char);
        
        public int Default => default(int);
        
        public double Default => default(double);
        
    }
}
```

### ディレクティブのサポートについて

T4では`<#@ DirectiveName [AttributeName = "AttributeValue"] ... #>`のような記法でコード生成に関する動作のカスタマイズなどが可能となっています。
SourceGeneratorT4では、以下のディレクティブをサポートしています。

- import
- include
- AppendReferenceAssemblies
- AppendGeneraterSource

importはT4と同じように機能します。

includeもT4とほぼ同じように機能します。ただし、SourceGeneratorT4のincludeは相対パスで自由なファイルを参照するのではなく、
プロジェクト内に配置されている拡張子が`.ttinc`のファイルのファイル名(※1)を指定して参照します。`.ttinc`ファイルはそれ自体はテキストテンプレートとして機能しませんが、
`.sgtt`ファイルから取り込まれているときは`.sgtt`と同じいように機能して取り込みもとのincludeディレクティブがおかれていた場所に自身の生成結果を挿入します。

> ※1 `.ttinc`ファイルがプロジェクト内でサブフォルダなどに置かれている場合であってもincludeでは相対パスを無視してファイル名のみで指定する必要があります。

AppendReferenceAssembliesはデバッグ・調査用のオプションです。出力ファイルの後方にテンプレート出力の実行時の参照アセンブリの一覧がコメント行で追記します。

AppendGeneraterSourceはデバッグ・調査用のオプションです。出力ファイルの後方にテンプレートから作られた内部的なコード生成プログラム自身のソースをコメント行で追記します。

## SourceGeneratorT4のコード生成が実行される仕組み

SourceGeneratorT4は既出の通りRoslynコンパイラのソースジェネレータとして実装されています。
SourceGeneratorT4はコンパイラの中でプロジェクトに含まれる`.sgtt`に対して以下のように動作します。

1. `.sgtt`をC#のコード生成ソースに変換
2. それを自身が呼び出されているコンパイルプロセスとは別の独自のソース生成用アセンブリとしてコンパイル
3. ソース生成用アセンブリを実行し`.sgtt`に対する生成結果となるソースコードを取得
4. 取得したソースコードを元のコンパイルプロセスのソースとして登録

## コード生成が実行されるときの.NETランタイムについて

ソース生成用アセンブリの実行はコンパイラが動作しているプロセス内で実行されます。SourceGeneratorT4がコンパイルのために新しいプロセスを作ることはありません。
そのため、SourceGeneratorT4のコード生成が行われるときの.NETランタイムはコンパイラが実行されている.NETランタイムとなります。
例えば、Visual Studioのインクリメンタルコンパイルから実行されている場合は`.NET Framework`のランタイムで実行され、dotnetコマンドのビルドでは`.NET`のランタイムで実行されます。
この性質により、テキストテンプレートのコード部分の書き方によってはdotnetコマンドではビルドできる一方で、Visual Studio上ではエラーになるなどの現象が発生する場合があります。

## インクリメンタルコンパイルなどに対する対応

SourceGeneratorT4はコード生成実行後に不要となったソース生成用アセンブリを都度プロセスからアンロードします。
そのため、Visual Studioのインクリメンタルコンパイルなどで使用されても、不要になったソース生成用アセンブリがランタイム内に不必要に残留することはありません。
