' ============================================================
' EstrategiaSeykota.vb
' Estrategia Ed Seykota: Canal de Donchian + ATR(14)
'
' Lógica de señal:
'   - Precio >= Upper Donchian → CALL
'   - Precio <= Lower Donchian → PUT
'   - Precio dentro del canal   → NEUTRAL
'
'   Confianza:
'   - 80% si el canal tiene pendiente en dirección de la señal
'   - 55% si el canal es lateral
'
'   Pendiente: compara upper/lower actual vs hace 3 velas.
'
' Usa IndicatorRegistry: DonchianChannel(periodo) y ATR(14)
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators

Namespace WpfDerivClientVB.Estrategias

    Public Class EstrategiaSeykota
        Implements IEstrategia

        Private ReadOnly _periodo As Integer

        Public Sub New(Optional periodo As Integer = 20)
            _periodo = periodo
        End Sub

        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return "Seykota"
            End Get
        End Property

        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return String.Format(
                    "Canal de Donchian({0}) + ATR(14). Inspirado en Ed Seykota. " &
                    "Señal CALL al romper el máximo de {0} períodos, PUT al romper el mínimo. " &
                    "Confianza 80% con pendiente, 55% canal lateral.", _periodo)
            End Get
        End Property

        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Dim lista = candles.ToList()
            Dim resultado As New EstrategiaResultado()

            If lista.Count < _periodo + 5 Then
                resultado.Motivo = String.Format("Insuficientes velas (mínimo {0})", _periodo + 5)
                Return resultado
            End If

            Dim last = lista.Count - 1

            ' Donchian + ATR del registry
            Dim dcRes  = New DonchianChannel(_periodo).Calculate(lista)
            Dim atrRes = New ATR(14).Calculate(lista)

            Dim dcCurr = dcRes(last)
            Dim dcPrev = dcRes(Math.Max(0, last - 3))

            Dim upper = GetVal(dcCurr, "Upper", 0D)
            Dim lower = GetVal(dcCurr, "Lower", 0D)
            Dim mid   = GetVal(dcCurr, "Mid",   (upper + lower) / 2D)

            Dim pUpper = GetVal(dcPrev, "Upper", upper)
            Dim pLower = GetVal(dcPrev, "Lower", lower)

            Dim slopeUp   = upper > pUpper
            Dim slopeDown = lower < pLower
            Dim atr14     = If(atrRes(last).IndicatorValue.HasValue, atrRes(last).IndicatorValue.Value, 0D)

            Dim currentClose = lista(last).ClosePrice

            If currentClose >= upper Then
                resultado.Senal = "CALL"
                If slopeUp Then
                    resultado.Confianza = 80
                    resultado.Motivo    = String.Format("Nuevo máximo {0}P | canal ascendente", _periodo)
                Else
                    resultado.Confianza = 55
                    resultado.Motivo    = String.Format("Nuevo máximo {0}P | canal lateral", _periodo)
                End If
            ElseIf currentClose <= lower Then
                resultado.Senal = "PUT"
                If slopeDown Then
                    resultado.Confianza = 80
                    resultado.Motivo    = String.Format("Nuevo mínimo {0}P | canal descendente", _periodo)
                Else
                    resultado.Confianza = 55
                    resultado.Motivo    = String.Format("Nuevo mínimo {0}P | canal lateral", _periodo)
                End If
            Else
                resultado.Motivo = String.Format("Precio dentro del canal DC {0:F4}–{1:F4}", lower, upper)
            End If

            resultado.Datos("Upper")    = upper
            resultado.Datos("Lower")    = lower
            resultado.Datos("Mid")      = mid
            resultado.Datos("ATR14")    = atr14
            resultado.Datos("SlopeUp")  = If(slopeUp, 1D, 0D)
            resultado.Datos("SlopeDown") = If(slopeDown, 1D, 0D)
            resultado.Datos("Precio")   = currentClose

            Return resultado
        End Function

        Private Shared Function GetVal(r As IndicatorResult, key As String, fallback As Decimal) As Decimal
            If r.AdditionalValues IsNot Nothing AndAlso
               r.AdditionalValues.ContainsKey(key) AndAlso
               r.AdditionalValues(key).HasValue Then
                Return r.AdditionalValues(key).Value
            End If
            Return fallback
        End Function

    End Class

End Namespace
