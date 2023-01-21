
using System.Globalization;

var cult = CultureInfo.GetCultureInfo("ja-JP");


CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

Console.WriteLine(DateTime.Now);