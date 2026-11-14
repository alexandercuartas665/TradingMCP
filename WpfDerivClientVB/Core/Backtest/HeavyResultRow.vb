' ============================================================
' HeavyResultRow.vb
'
' DTO de una fila del DataGrid central del Heavy Strategy Tester.
' Construido a partir de un HeavyTestJob completado y su BacktestResultado.
' ============================================================
Imports System.ComponentModel
Imports System.Windows.Media

Namespace WpfDerivClientVB.Backtesting

    Public Class HeavyResultRow
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        Private Sub Notify(name As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        ''' <summary>Marcado por el usuario via checkbox en el DataGrid.</summary>
        Private _selected As Boolean = False
        Public Property Selected As Boolean
            Get
                Return _selected
            End Get
            Set(value As Boolean)
                If _selected = value Then Return
                _selected = value
                Notify("Selected")
            End Set
        End Property

        ''' <summary>Id de la sesion en BD (0 si aun no esta persistida).</summary>
        Public Property SesionId As Integer = 0
        Public Property Rank As Integer = 0
        Public Property Estrategia As String = ""
        Public Property Activo As String = ""
        Public Property Timeframe As String = ""
        Public Property Conf As Integer = 0
        Public Property Pay As Decimal = 0D
        Public Property Dur As Integer = 0
        Public Property Cap As Decimal = 0D
        Public Property Ops As Integer = 0
        Public Property OpsSem As Decimal = 0D
        Public Property Win As Decimal = 0D       ' 0..100 (porcentaje)
        Public Property Pnl As Decimal = 0D
        Public Property DD As Decimal = 0D        ' 0..100 (porcentaje)
        Public Property Sharpe As Decimal = 0D
        Public Property Estado As String = ""
        Public Property FechaInicio As DateTime = DateTime.MinValue
        Public Property FechaFin As DateTime = DateTime.MinValue

        ''' <summary>Referencia al job para abrir el drawer al hacer click.</summary>
        Public Property JobRef As HeavyTestJob = Nothing

        ' ── Properties calculadas para presentacion ──────────────────
        Public ReadOnly Property RankStr As String
            Get
                Return "#" & Rank
            End Get
        End Property
        Public ReadOnly Property ConfStr As String
            Get
                Return Conf & "%"
            End Get
        End Property
        Public ReadOnly Property PayStr As String
            Get
                Return Pay.ToString("F0") & "%"
            End Get
        End Property
        Public ReadOnly Property CapStr As String
            Get
                Return "$" & Cap.ToString("N0")
            End Get
        End Property
        Public ReadOnly Property OpsSemStr As String
            Get
                Return OpsSem.ToString("F1")
            End Get
        End Property
        Public ReadOnly Property WinStr As String
            Get
                Return Win.ToString("F1") & "%"
            End Get
        End Property
        Public ReadOnly Property PnlStr As String
            Get
                Return Pnl.ToString("+0.00;-0.00;0.00")
            End Get
        End Property
        Public ReadOnly Property DDStr As String
            Get
                Return DD.ToString("F1") & "%"
            End Get
        End Property
        Public ReadOnly Property SharpeStr As String
            Get
                Return Sharpe.ToString("F2")
            End Get
        End Property
        Public ReadOnly Property InicioStr As String
            Get
                If FechaInicio = DateTime.MinValue Then Return ""
                Return FechaInicio.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
        Public ReadOnly Property FinStr As String
            Get
                If FechaFin = DateTime.MinValue Then Return ""
                Return FechaFin.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
        Public ReadOnly Property DuracionStr As String
            Get
                If FechaInicio = DateTime.MinValue OrElse FechaFin = DateTime.MinValue Then Return ""
                Dim ts = FechaFin - FechaInicio
                If ts.TotalSeconds < 0 Then Return ""
                If ts.TotalMinutes < 1 Then Return ts.Seconds & "s"
                If ts.TotalHours < 1 Then Return ts.Minutes & "m " & ts.Seconds & "s"
                Return CInt(Math.Floor(ts.TotalHours)) & "h " & ts.Minutes & "m"
            End Get
        End Property
        Public ReadOnly Property FechaInicioStr As String
            Get
                If FechaInicio = DateTime.MinValue Then Return ""
                Return FechaInicio.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            End Get
        End Property
        Public ReadOnly Property FechaFinStr As String
            Get
                If FechaFin = DateTime.MinValue Then Return ""
                Return FechaFin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            End Get
        End Property

        Public ReadOnly Property PnlBrush As Brush
            Get
                If Pnl > 0D Then Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
                If Pnl < 0D Then Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
                Return New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
            End Get
        End Property

        Public ReadOnly Property WinBrush As Brush
            Get
                If Win >= 50D Then Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
                Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            End Get
        End Property

        ''' <summary>True si la combinacion es "ganadora": win >= 50% AND pnl > 0.</summary>
        Public ReadOnly Property EsGanadora As Boolean
            Get
                Return Win >= 50D AndAlso Pnl > 0D
            End Get
        End Property

        ''' <summary>Construye una fila a partir de un job completado.</summary>
        Public Shared Function DesdeJob(job As HeavyTestJob) As HeavyResultRow
            Dim r As New HeavyResultRow With {
                .Estrategia = job.Estrategia,
                .Activo = job.Symbol,
                .Timeframe = job.Timeframe,
                .Conf = job.Confianza,
                .Pay = job.Payout,
                .Dur = job.Duracion,
                .Cap = job.Capital,
                .Estado = job.EstadoStr,
                .FechaInicio = job.FechaInicio,
                .FechaFin = job.FechaFin,
                .JobRef = job
            }
            If job.Resultado IsNot Nothing AndAlso job.Resultado.Sesion IsNot Nothing Then
                Dim ses = job.Resultado.Sesion
                r.SesionId = ses.Id
                r.Ops = ses.TotalOperaciones
                r.Win = ses.WinRate * 100D
                r.Pnl = ses.ProfitLossTotal
                r.DD = ses.MaxDrawdown
                r.OpsSem = HeavyStatsCalculator.OpsPromedioSemanal(job.Resultado.Operaciones)
                r.Sharpe = HeavyStatsCalculator.Sharpe(job.Resultado.Operaciones)
            End If
            Return r
        End Function

        ''' <summary>
        ''' Construye una fila a partir de una sesion ya persistida en BD (sin operaciones cargadas).
        ''' Sharpe/OpsSem quedan en 0 hasta que el usuario abra el drawer y se carguen las ops.
        ''' </summary>
        Public Shared Function DesdeSesion(s As BacktestSesion) As HeavyResultRow
            If s Is Nothing Then Return Nothing
            Dim r As New HeavyResultRow With {
                .SesionId = s.Id,
                .Estrategia = s.Estrategia,
                .Activo = s.Symbol,
                .Timeframe = s.Timeframe,
                .Conf = s.MinConfianza,
                .Pay = s.PayoutPct,
                .Dur = s.DuracionVelas,
                .Cap = 0D,
                .Ops = s.TotalOperaciones,
                .Win = s.WinRate * 100D,
                .Pnl = s.ProfitLossTotal,
                .DD = s.MaxDrawdown,
                .Estado = "GUARDADA",
                .FechaInicio = s.CreatedAt,
                .FechaFin = s.CreatedAt
            }
            Return r
        End Function
    End Class

End Namespace
