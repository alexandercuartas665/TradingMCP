-- =========================================================
--  BASE DE DATOS: traiding_db
--  Script de inicializacion de tablas
-- =========================================================

-- Tabla principal de velas OHLC
CREATE TABLE IF NOT EXISTS public.candles (
    id           BIGSERIAL       PRIMARY KEY,
    platform     VARCHAR(30)     NOT NULL,
    symbol       VARCHAR(30)     NOT NULL,
    timeframe    VARCHAR(10)     NOT NULL,
    epoch        BIGINT          NOT NULL,
    open_time    TIMESTAMPTZ     NOT NULL,
    open_price   NUMERIC(20, 8)  NOT NULL,
    high_price   NUMERIC(20, 8)  NOT NULL,
    low_price    NUMERIC(20, 8)  NOT NULL,
    close_price  NUMERIC(20, 8)  NOT NULL,
    is_closed    BOOLEAN         NOT NULL DEFAULT FALSE,
    created_at   TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

-- Constraint de deduplicacion: misma vela no se puede insertar dos veces
ALTER TABLE public.candles
    ADD CONSTRAINT uq_candles_unique
    UNIQUE (platform, symbol, timeframe, epoch);

-- Indices para consultas frecuentes por activo y tiempo
CREATE INDEX IF NOT EXISTS idx_candles_symbol_tf_epoch
    ON public.candles (platform, symbol, timeframe, epoch DESC);

CREATE INDEX IF NOT EXISTS idx_candles_open_time
    ON public.candles (open_time DESC);

-- Comentarios de documentacion
COMMENT ON TABLE public.candles IS 'Velas OHLC de cualquier plataforma y activo financiero';
COMMENT ON COLUMN public.candles.platform  IS 'Plataforma de origen: Deriv, Binance, etc.';
COMMENT ON COLUMN public.candles.symbol    IS 'Simbolo del activo: R_100, BTCUSDT, etc.';
COMMENT ON COLUMN public.candles.timeframe IS 'Temporalidad: 1m, 5m, 15m, 1h, 1d, etc.';
COMMENT ON COLUMN public.candles.epoch     IS 'Unix timestamp de apertura de la vela (segundos)';
COMMENT ON COLUMN public.candles.is_closed IS 'TRUE si la vela ya cerro, FALSE si sigue viva';

-- Vista de resumen para consultas rapidas
CREATE OR REPLACE VIEW public.vw_candles_summary AS
SELECT
    platform,
    symbol,
    timeframe,
    COUNT(*)                        AS total_candles,
    MIN(open_time)                  AS desde,
    MAX(open_time)                  AS hasta,
    MAX(created_at)                 AS ultima_actualizacion
FROM public.candles
GROUP BY platform, symbol, timeframe
ORDER BY platform, symbol, timeframe;

COMMENT ON VIEW public.vw_candles_summary IS 'Resumen de datos disponibles por plataforma, activo y temporalidad';
