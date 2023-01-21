# AltT4SourceGenerator

AltT4SourceGeneratorはT4の構文でデザイン時ソース生成を行うことができるソースジェネレータです。

## 使い方

### パッケージ参照

プロジェクトの`.csproj`に以下の`PackageReference`を追加します。

```xml
<PackageReference Include="Benutomo.AltT4SourceGenerator" Version="1.1.0" PrivateAssets="true" />
```

### 基本的な使用方法

拡張子が`.sgtt`のファイルがAltT4SourceGeneratorのテキストテンプレートファイルとして扱われます。
プロジェクトフォルダに含まれる`.sgtt`は自動的にAdditionalFilesに取り込まれます。`.csproj`に明示的な記載は不要です。

基本的な構文はT4を踏襲しています。コードの断片は`<# statement_faragment #>`で埋め込むことが可能です。また、式の評価結果も`<#= expression #>`で埋め込み可能です。なお、AltT4SourceGeneratorでクラス機能ブロック(`<#+ member_difinition #>`)を使用することはできません。

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

Visual Studioではこのように見えます。

<img width="270" alt="image" src="https://user-images.githubusercontent.com/52617232/213888935-e8e1010b-9951-4b13-9b40-0f8a048abfaa.png">

## ディレクティブのサポートについて

T4では`<#@ DirectiveName [AttributeName = "AttributeValue"] ... #>`のような記法でコード生成に関する動作のカスタマイズなどが可能となっています。
AltT4SourceGeneratorでは、以下のディレクティブをサポートしています。

- import
- include
- AppendReferenceAssemblies
- AppendGeneraterSource

importはT4と同じように機能します。

includeもT4とほぼ同じように機能します。ただし、AltT4SourceGeneratorのincludeは相対パスで自由なファイルを参照するのではなく、
プロジェクト内に配置されている拡張子が`.ttinc`のファイルのファイル名(※1)を指定して参照します。`.ttinc`ファイルはそれ自体はテキストテンプレートとして機能しませんが、
`.sgtt`ファイルから取り込まれているときは`.sgtt`と同じいように機能して取り込みもとのincludeディレクティブがおかれていた場所に自身の生成結果を挿入します。

> ※1 `.ttinc`ファイルがプロジェクト内でサブフォルダなどに置かれている場合であってもincludeでは相対パスを無視してファイル名のみで指定する必要があります。

AppendReferenceAssembliesはデバッグ・調査用のオプションです。出力ファイルの後方にテンプレート出力の実行時の参照アセンブリの一覧がコメント行で追記します。

AppendGeneraterSourceはデバッグ・調査用のオプションです。出力ファイルの後方にテンプレートから作られた内部的なコード生成プログラム自身のソースをコメント行で追記します。

## AltT4SourceGeneratorのコード生成が実行される仕組み

AltT4SourceGeneratorはRoslynコンパイラのソースジェネレータとして実装されています。
AltT4SourceGeneratorはコンパイラの中でプロジェクトに含まれる`.sgtt`に対して以下のように動作します。

1. `.sgtt`をC#のコード生成ソースに変換
2. コード生成ソースをコンパイルしソース生成用アセンブリを生成<br>(自分自身が呼び出されているコンパイルプロセスとは別の独立したコンパイルを行う)
3. ソース生成用アセンブリを実行し`.sgtt`に対する最終的な生成結果となるソースコードを生成
4. 生成したソースコードを元のコンパイルプロセスのソースコードファイルとして登録

## コード生成時のカルチャ

AltT4SourceGeneratorのコード生成時のデフォルトのカルチャはInvariantCultureとなります。
カルチャを指定する場合は`.csproj`に以下のような設定を追加します。

```xml
  <PropertyGroup>
    <AltT4SourceGeneratorDefaultCulture>ja_JP</AltT4SourceGeneratorDefaultCulture>
  </PropertyGroup>
```

## コード生成が実行されるときの.NETランタイムについて

ソース生成用アセンブリの実行はコンパイラが動作しているプロセス内で実行されます。AltT4SourceGeneratorがコンパイルのために新しいプロセスを作ることはありません。
そのため、AltT4SourceGeneratorのコード生成が行われるときの.NETランタイムはコンパイラが実行されている.NETランタイムとなります。
例えば、Visual Studioのインクリメンタルコンパイルから実行されている場合は`.NET Framework`のランタイムで実行され、dotnetコマンドのビルドでは`.NET`のランタイムで実行されます。
この性質により、テキストテンプレートのコード部分の書き方によってはdotnetコマンドではビルドできる一方で、Visual Studio上ではエラーになるなどの現象が発生する場合があります。

## インクリメンタルコンパイルなどに対する対応

AltT4SourceGeneratorはコード生成実行後に不要となったソース生成用アセンブリを都度プロセスからアンロードします。
そのため、Visual Studioのインクリメンタルコンパイルなどで使用されても、不要になったソース生成用アセンブリが常駐しているコンパイラのランタイム内に不必要に残留することはありません。

## AltT4SourceGeneratorのメリット

原則として、AltT4SourceGeneratorでできることはT4でもできます。ただし、ソースジェネレータベースであることによるいくつかのメリットもあります。

### .csprojの簡潔さ

T4を使用するときに`.csproj`に生じる以下のような記載がAltT4SourceGeneratorでは不要になり、`.csproj`がすっきりします。

```xml
  <ItemGroup>
    <None Update="TextTemplate1.tt">
      <Generator>TextTemplatingFileGenerator</Generator>
      <LastGenOutput>TextTemplate1.txt</LastGenOutput>
    </None>
    <None Update="TextTemplate1.txt">
      <DesignTime>True</DesignTime>
      <AutoGen>True</AutoGen>
      <DependentUpon>TextTemplate1.tt</DependentUpon>
    </None>
  </ItemGroup>

  <ItemGroup>
    <Service Include="{508349b6-6b84-4df5-91f0-309beebad82d}" />
  </ItemGroup>
```

### includeファイルの変更が即時反映

T4はある`.tt`でインクルードされているファイルに変更を行ってもインクルードしている側の`.tt`のテンプレート変換が再実行されるまで生成結果は更新されません。AltT4SourceGeneratorの場合は`<#@ include="xxx.ttinc" #>`で取り込んでいるファイルの変更が`.sgtt`側の生成結果に即座に反映されます。
T4の場合は、ビルドごとに常にテンプレート変換を再実行するような設定をしない限り、インクルードされているファイルに行った変更が反映されない状態でコンパイルしてしまうミスが起きることがありますが、AltT4SourceGeneratorの場合は`.sgtt`と`.ttinc`常に最新の生成結果でコンパイルが実行されます。

### 生成元のテキストテンプレート以外の要因で生成されたソースファイルの内容が変更されない

T4の場合は、出力結果が通常のソースコードファイルとして出力されるため、一括置換やコードジャンプなどから無意識におこなう変更などで出力後のファイルだけが書き換わり、後になってからテンプレート変換の再実行を行ったときに出力ファイルに直接行われてしまっていた変更が取り消されてコンパイルエラーになるような事故が起きる場合があります。
AltT4SourceGeneratorの場合は、基本的に生成したソースコードに変更可能な実体となるファイルがないため、生成元のテンプレートファイル以外の要素から変更される心配がなく、そのような事故が原理的に起きなくなります。
