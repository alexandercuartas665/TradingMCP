' ============================================================
' EstrategiaCacheada.vb
'
' Envuelve una estrategia y memoriza su resultado por ventana de
' velas (clave: epoch de la primera y la ultima vela). El analisis
' es lo mas costoso del backtest y, en el walk-forward, se repite
' con las mismas velas para cada umbral de confianza y en ventanas
' solapadas. Solo valido mientras las velas no cambien (no usar con
' precios con ruido).
' ============================================================
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    Public Class EstrategiaCacheada
        Implements IEstrategia

        Private ReadOnly _interna As IEstrategia
        Private ReadOnly _cache As New ConcurrentDictionary(Of String, EstrategiaResultado)()

        Public Sub New(interna As IEstrategia)
            _interna = interna
        End Sub

        Public ReadOnly Property Aciertos As Integer
        Public ReadOnly Property Calculos As Integer

        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return _interna.Nombre
            End Get
        End Property

        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return _interna.Descripcion
            End Get
        End Property

        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Dim lista = TryCast(candles, IList(Of CandleModel))
            If lista Is Nothing Then lista = candles.ToList()
            If lista.Count = 0 Then Return _interna.Analizar(lista)
            Dim clave = lista(0).Epoch.ToString() & ":" & lista(lista.Count - 1).Epoch.ToString() & ":" & lista.Count.ToString()
            Dim r As EstrategiaResultado = Nothing
            If _cache.TryGetValue(clave, r) Then
                _Aciertos += 1
                Return r
            End If
            r = _interna.Analizar(lista)
            _cache(clave) = r
            _Calculos += 1
            Return r
        End Function

    End Class

End Namespace
