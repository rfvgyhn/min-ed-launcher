module MinEdLauncher.RunningTime

open System.Globalization

module private RunningTimeFormatting =
    let numberFormat = CultureInfo.InvariantCulture.NumberFormat.Clone() :?> NumberFormatInfo
    do
        numberFormat.NumberDecimalSeparator <- "."
        numberFormat.NumberDecimalDigits <- 5

[<Struct>]
type RunningTime =
    | RunningTime of double

    override this.ToString() =
        match this with
        | RunningTime time -> time.ToString("F", RunningTimeFormatting.numberFormat)
