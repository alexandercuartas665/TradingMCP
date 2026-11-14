' ============================================================
' HeavyStatsCalculator.vb
'
' Funciones puras para computar metricas avanzadas a partir de
' BacktestResultado. Usado por el drawer detalle del Heavy Tester.
' ============================================================
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq

Namespace WpfDerivClientVB.Backtesting

    Public Module HeavyStatsCalculator

        ''' <summary>Convierte epoch UNIX a DateTime UTC.</summary>
        Public Function EpochToUtc(epoch As Long) As DateTime
            Return New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch)
        End Function

        ''' <summary>Clave de semana ISO (yyyy-Www).</summary>
        Public Function ClaveSemanaIso(dt As DateTime) As String
            Dim cal = CultureInfo.InvariantCulture.Calendar
            Dim wk = cal.GetWeekOfYear(dt, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday)
            Return String.Format("{0:D4}-W{1:D2}", dt.Year, wk)
        End Function

        ''' <summary>Cantidad de operaciones por semana ISO.</summary>
        Public Function OpsPorSemana(ops As IEnumerable(Of BacktestOperacion)) As Dictionary(Of String, Integer)
            Dim r As New Dictionary(Of String, Integer)()
            If ops Is Nothing Then Return r
            For Each o In ops
                Dim k = ClaveSemanaIso(EpochToUtc(o.EpochEntrada))
                If r.ContainsKey(k) Then r(k) += 1 Else r(k) = 1
            Next
            Return r
        End Function

        ''' <summary>Win-rate por semana ISO (0..1).</summary>
        Public Function WinRateSemanal(ops As IEnumerable(Of BacktestOperacion)) As Dictionary(Of String, Decimal)
            Dim r As New Dictionary(Of String, Decimal)()
            If ops Is Nothing Then Return r
            Dim grupos = ops.GroupBy(Function(o) ClaveSemanaIso(EpochToUtc(o.EpochEntrada)))
            For Each g In grupos
                Dim total = g.Count()
                Dim ganadas = g.Count(Function(o) o.Estado = "Ganado")
                r(g.Key) = If(total > 0, CDec(ganadas) / CDec(total), 0D)
            Next
            Return r
        End Function

        ''' <summary>Ops promedio por semana (todas las semanas con operaciones).</summary>
        Public Function OpsPromedioSemanal(ops As IEnumerable(Of BacktestOperacion)) As Decimal
            Dim porSem = OpsPorSemana(ops)
            If porSem.Count = 0 Then Return 0D
            Return CDec(porSem.Values.Sum()) / CDec(porSem.Count)
        End Function

        ''' <summary>Curva de equity acumulada en orden temporal.</summary>
        Public Function EquityCurve(ops As IEnumerable(Of BacktestOperacion), capInicial As Decimal) _
                As List(Of (Epoch As Long, Equity As Decimal))
            Dim r As New List(Of (Epoch As Long, Equity As Decimal))()
            If ops Is Nothing Then Return r
            Dim equity As Decimal = capInicial
            For Each o In ops.OrderBy(Function(x) x.EpochEntrada)
                equity += o.ProfitLoss
                r.Add((o.EpochEntrada, equity))
            Next
            Return r
        End Function

        ''' <summary>Drawdown rolling: para cada punto del equity, % drop desde el peak previo.</summary>
        Public Function DrawdownRolling(equity As List(Of (Epoch As Long, Equity As Decimal))) _
                As List(Of (Epoch As Long, DDPct As Decimal))
            Dim r As New List(Of (Epoch As Long, DDPct As Decimal))()
            If equity Is Nothing OrElse equity.Count = 0 Then Return r
            Dim peak As Decimal = equity(0).Equity
            For Each p In equity
                If p.Equity > peak Then peak = p.Equity
                Dim dd As Decimal = If(peak > 0D, (p.Equity - peak) / peak * 100D, 0D)
                r.Add((p.Epoch, dd))
            Next
            Return r
        End Function

        ''' <summary>Drawdown maximo (porcentaje, valor positivo).</summary>
        Public Function DrawdownMaxPct(equity As List(Of (Epoch As Long, Equity As Decimal))) As Decimal
            Dim dd = DrawdownRolling(equity)
            If dd.Count = 0 Then Return 0D
            Return Math.Abs(dd.Min(Function(x) x.DDPct))
        End Function

        ''' <summary>Sharpe simple: media de returns / desviacion estandar.</summary>
        Public Function Sharpe(ops As IEnumerable(Of BacktestOperacion)) As Decimal
            If ops Is Nothing Then Return 0D
            Dim returns = ops.Select(Function(o) CDbl(o.ProfitLoss)).ToList()
            If returns.Count < 2 Then Return 0D
            Dim media = returns.Average()
            Dim varianza = returns.Sum(Function(x) (x - media) * (x - media)) / (returns.Count - 1)
            Dim desv = Math.Sqrt(varianza)
            If desv = 0 Then Return 0D
            Return CDec(media / desv)
        End Function

        ''' <summary>
        ''' Histograma de PnL en `bins` cubos uniformes entre min y max.
        ''' Retorna lista de (centroBin, count).
        ''' </summary>
        Public Function DistribucionPnl(ops As IEnumerable(Of BacktestOperacion), bins As Integer) _
                As List(Of (Centro As Decimal, Count As Integer))
            Dim r As New List(Of (Centro As Decimal, Count As Integer))()
            If ops Is Nothing OrElse bins <= 0 Then Return r
            Dim valores = ops.Select(Function(o) o.ProfitLoss).ToList()
            If valores.Count = 0 Then Return r
            Dim mn = valores.Min()
            Dim mx = valores.Max()
            If mx = mn Then
                r.Add((mn, valores.Count))
                Return r
            End If
            Dim ancho = (mx - mn) / CDec(bins)
            Dim cubos(bins - 1) As Integer
            For Each v In valores
                Dim idx As Integer = CInt(Math.Floor((v - mn) / ancho))
                If idx >= bins Then idx = bins - 1
                If idx < 0 Then idx = 0
                cubos(idx) += 1
            Next
            For i As Integer = 0 To bins - 1
                Dim centro = mn + ancho * (i + 0.5D)
                r.Add((centro, cubos(i)))
            Next
            Return r
        End Function

        ''' <summary>Profit factor: suma ganancias / suma perdidas absolutas.</summary>
        Public Function ProfitFactor(ops As IEnumerable(Of BacktestOperacion)) As Decimal
            If ops Is Nothing Then Return 0D
            Dim gan = ops.Where(Function(o) o.ProfitLoss > 0D).Sum(Function(o) o.ProfitLoss)
            Dim per = Math.Abs(ops.Where(Function(o) o.ProfitLoss < 0D).Sum(Function(o) o.ProfitLoss))
            If per = 0D Then Return If(gan > 0D, 999D, 0D)
            Return gan / per
        End Function

    End Module

End Namespace
