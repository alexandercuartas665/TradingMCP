' ================================================================
'  CandleBar — Modelo de datos compartido para CandleManager
'  Archivo independiente para evitar conflictos de referencia
' ================================================================
Public Class CandleBar
    Public Property Time As DateTime
    Public Property Open As Double
    Public Property High As Double
    Public Property Low As Double
    Public Property Close As Double
    Public Property Volume As Double
    ''' <summary>
    ''' Volumen máximo del batch actual (para normalizar barras de volumen).
    ''' Se asigna externamente tras cargar la serie completa.
    ''' </summary>
    Public Property MaxVolume As Double
End Class
