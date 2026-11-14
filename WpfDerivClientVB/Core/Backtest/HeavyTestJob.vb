' ============================================================
' HeavyTestJob.vb
'
' Representa un job individual dentro de la cola del Heavy Strategy
' Tester. Cada job es una combinacion unica:
'   (Estrategia, Symbol, Timeframe, Confianza, Payout, Duracion, Capital, Periodo)
'
' Implementa INotifyPropertyChanged para que las cards del panel
' derecho se actualicen automaticamente cuando cambia Estado/Progreso.
' ============================================================
Imports System.ComponentModel
Imports System.Windows.Media

Namespace WpfDerivClientVB.Backtesting

    Public Enum HeavyJobEstado
        Pendiente
        Ejecutando
        Completado
        Error_
        Cancelado
    End Enum

    Public Class HeavyTestJob
        Implements INotifyPropertyChanged

        Public Property Id As String = Guid.NewGuid().ToString().Substring(0, 8).ToUpper()
        Public Property Estrategia As String = ""
        Public Property Symbol As String = ""
        Public Property Timeframe As String = ""
        Public Property Confianza As Integer = 0
        Public Property Payout As Decimal = 0D
        ''' <summary>True: usar el payout real medido (PayoutsReales) si existe para este simbolo y duracion.</summary>
        Public Property UsarPayoutReal As Boolean = False
        ''' <summary>True si Payout es un valor medido en Deriv (no supuesto).</summary>
        Public Property PayoutEsReal As Boolean = False
        Public Property Duracion As Integer = 0
        Public Property Capital As Decimal = 0D
        Public Property DesdeEpoch As Long = 0L
        Public Property HastaEpoch As Long = 0L

        Private _estado As HeavyJobEstado = HeavyJobEstado.Pendiente
        Public Property Estado As HeavyJobEstado
            Get
                Return _estado
            End Get
            Set(value As HeavyJobEstado)
                If _estado = value Then Return
                _estado = value
                Notify("Estado")
                Notify("EstadoStr")
                Notify("EstadoBrush")
                Notify("IconoEstado")
                Notify("EsPendiente")
                Notify("EsEjecutando")
                Notify("EsCompletado")
            End Set
        End Property

        Private _progreso As Decimal = 0D
        ''' <summary>0..100</summary>
        Public Property Progreso As Decimal
            Get
                Return _progreso
            End Get
            Set(value As Decimal)
                If _progreso = value Then Return
                _progreso = Math.Max(0D, Math.Min(100D, value))
                Notify("Progreso")
                Notify("ProgresoStr")
            End Set
        End Property

        Public Property MensajeError As String = ""
        Public Property Resultado As BacktestResultado = Nothing

        Private _fechaInicio As DateTime = DateTime.MinValue
        Public Property FechaInicio As DateTime
            Get
                Return _fechaInicio
            End Get
            Set(value As DateTime)
                _fechaInicio = value
                Notify("FechaInicio")
                Notify("FechaInicioStr")
                Notify("TiemposStr")
                Notify("DuracionStr")
            End Set
        End Property

        Private _fechaFin As DateTime = DateTime.MinValue
        Public Property FechaFin As DateTime
            Get
                Return _fechaFin
            End Get
            Set(value As DateTime)
                _fechaFin = value
                Notify("FechaFin")
                Notify("FechaFinStr")
                Notify("TiemposStr")
                Notify("DuracionStr")
            End Set
        End Property

        ' ── Progreso en vivo durante la ejecucion ──────────────────────
        Private _velasAnalizadas As Integer = 0
        Public Property VelasAnalizadas As Integer
            Get
                Return _velasAnalizadas
            End Get
            Set(value As Integer)
                If _velasAnalizadas = value Then Return
                _velasAnalizadas = value
                Notify("VelasAnalizadas")
                Notify("VelasStr")
            End Set
        End Property

        Private _totalVelas As Integer = 0
        Public Property TotalVelas As Integer
            Get
                Return _totalVelas
            End Get
            Set(value As Integer)
                If _totalVelas = value Then Return
                _totalVelas = value
                Notify("TotalVelas")
                Notify("VelasStr")
            End Set
        End Property

        Private _ganadasParcial As Integer = 0
        Public Property GanadasParcial As Integer
            Get
                Return _ganadasParcial
            End Get
            Set(value As Integer)
                If _ganadasParcial = value Then Return
                _ganadasParcial = value
                Notify("GanadasParcial")
                Notify("ResultadoEnVivoStr")
            End Set
        End Property

        Private _perdidasParcial As Integer = 0
        Public Property PerdidasParcial As Integer
            Get
                Return _perdidasParcial
            End Get
            Set(value As Integer)
                If _perdidasParcial = value Then Return
                _perdidasParcial = value
                Notify("PerdidasParcial")
                Notify("ResultadoEnVivoStr")
            End Set
        End Property

        ' ── Properties de presentacion para el binding ───────────────────
        Public ReadOnly Property EstadoStr As String
            Get
                Select Case _estado
                    Case HeavyJobEstado.Pendiente : Return "PENDIENTE"
                    Case HeavyJobEstado.Ejecutando : Return "EJECUTANDO"
                    Case HeavyJobEstado.Completado : Return "COMPLETADO"
                    Case HeavyJobEstado.Error_ : Return "ERROR"
                    Case HeavyJobEstado.Cancelado : Return "CANCELADO"
                    Case Else : Return ""
                End Select
            End Get
        End Property

        Public ReadOnly Property EstadoBrush As Brush
            Get
                Select Case _estado
                    Case HeavyJobEstado.Pendiente
                        Return New SolidColorBrush(Color.FromRgb(&H6C, &H77, &H90))
                    Case HeavyJobEstado.Ejecutando
                        Return New SolidColorBrush(Color.FromRgb(&HFF, &HB4, &H54))
                    Case HeavyJobEstado.Completado
                        Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
                    Case HeavyJobEstado.Error_
                        Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
                    Case Else
                        Return New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
                End Select
            End Get
        End Property

        Public ReadOnly Property IconoEstado As String
            Get
                Select Case _estado
                    Case HeavyJobEstado.Pendiente : Return "⏳"
                    Case HeavyJobEstado.Ejecutando : Return "▶"
                    Case HeavyJobEstado.Completado : Return "✓"
                    Case HeavyJobEstado.Error_ : Return "✕"
                    Case HeavyJobEstado.Cancelado : Return "⊘"
                    Case Else : Return ""
                End Select
            End Get
        End Property

        Public ReadOnly Property EsPendiente As Boolean
            Get
                Return _estado = HeavyJobEstado.Pendiente
            End Get
        End Property
        Public ReadOnly Property EsEjecutando As Boolean
            Get
                Return _estado = HeavyJobEstado.Ejecutando
            End Get
        End Property
        Public ReadOnly Property EsCompletado As Boolean
            Get
                Return _estado = HeavyJobEstado.Completado
            End Get
        End Property

        Public ReadOnly Property NombreCorto As String
            Get
                Return String.Format("{0} · {1}/{2}", Estrategia, Symbol, Timeframe)
            End Get
        End Property

        Public ReadOnly Property MetaStr As String
            Get
                Return String.Format("conf:{0}% pay:{1}% dur:{2}v cap:${3:N0}",
                                     Confianza, Payout, Duracion, Capital)
            End Get
        End Property

        Public ReadOnly Property ProgresoStr As String
            Get
                Return String.Format("{0:F0}%", _progreso)
            End Get
        End Property

        ''' <summary>"📊 1,234 / 5,678 velas" — para mostrar en la card.</summary>
        Public ReadOnly Property VelasStr As String
            Get
                If _totalVelas > 0 Then
                    Return String.Format("{0:N0} / {1:N0} velas", _velasAnalizadas, _totalVelas)
                End If
                Return If(_velasAnalizadas > 0, String.Format("{0:N0} velas", _velasAnalizadas), "")
            End Get
        End Property

        ''' <summary>"HH:mm:ss" hora inicio (vacio si no inicio).</summary>
        Public ReadOnly Property FechaInicioStr As String
            Get
                If _fechaInicio = DateTime.MinValue Then Return ""
                Return _fechaInicio.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property

        ''' <summary>"HH:mm:ss" hora fin (vacio si no termino).</summary>
        Public ReadOnly Property FechaFinStr As String
            Get
                If _fechaFin = DateTime.MinValue Then Return ""
                Return _fechaFin.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property

        ''' <summary>"⏱ 14:32:01 → 14:33:25" para card.</summary>
        Public ReadOnly Property TiemposStr As String
            Get
                If _fechaInicio = DateTime.MinValue Then Return ""
                Dim ini = _fechaInicio.ToLocalTime().ToString("HH:mm:ss")
                If _fechaFin = DateTime.MinValue Then
                    Return "⏱ " & ini & " → ..."
                End If
                Return "⏱ " & ini & " → " & _fechaFin.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property

        ''' <summary>"1m 24s" duracion total del job.</summary>
        Public ReadOnly Property DuracionStr As String
            Get
                If _fechaInicio = DateTime.MinValue Then Return ""
                Dim fin = If(_fechaFin = DateTime.MinValue, DateTime.UtcNow, _fechaFin)
                Dim ts = fin - _fechaInicio
                If ts.TotalSeconds < 0 Then Return ""
                If ts.TotalMinutes < 1 Then Return ts.Seconds & "s"
                If ts.TotalHours < 1 Then Return ts.Minutes & "m " & ts.Seconds & "s"
                Return CInt(Math.Floor(ts.TotalHours)) & "h " & ts.Minutes & "m"
            End Get
        End Property

        ''' <summary>"✓ 12 G  ✕ 8 P" — score en vivo para card.</summary>
        Public ReadOnly Property ResultadoEnVivoStr As String
            Get
                If _ganadasParcial = 0 AndAlso _perdidasParcial = 0 Then Return ""
                Return String.Format("✓{0}G  ✕{1}P", _ganadasParcial, _perdidasParcial)
            End Get
        End Property

        ' ── INPC ───────────────────────────────────────────────────────────
        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        Private Sub Notify(name As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub
    End Class

End Namespace
