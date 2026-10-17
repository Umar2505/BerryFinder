import 'maplibre-gl/dist/maplibre-gl.css';
import './berry-map.css';

import maplibregl, { type GeoJSONSource, type Map as MapLibreMap, type MapGeoJSONFeature } from 'maplibre-gl';
import { memo, useEffect, useRef } from 'react';
import { type IkonUiComponentResolver, type UiComponentRendererProps, useUiNode } from '@ikonai/sdk-react-ui';

// Suitability ramps per species: bilberry in blue-purple, lingonberry in red, so the two maps never look alike
const RAMPS: Record<string, Record<string, string>> = {
  bilberry: { high: '#3f2a86', moderate: '#7b6cc0', low: '#c9c1e6', unlikely: '#b9b4a8', not_assessed: '#8f8a80' },
  lingonberry: { high: '#a3172f', moderate: '#d9566c', low: '#f0b7c0', unlikely: '#b9b4a8', not_assessed: '#8f8a80' },
};

// Other berries get a ramp from their own colour (sent by the app): the colour itself, then lighter mixes toward white
function mixWithWhite(hex: string, amount: number): string {
  const n = parseInt(hex.slice(1), 16);
  const channel = (shift: number) => Math.round(((n >> shift) & 255) + (255 - ((n >> shift) & 255)) * amount);
  return `#${[16, 8, 0].map((s) => channel(s).toString(16).padStart(2, '0')).join('')}`;
}

function rampFor(species: string, color: string | null): Record<string, string> {
  if (RAMPS[species]) return RAMPS[species];
  if (color && /^#[0-9a-fA-F]{6}$/.test(color)) {
    return { high: color, moderate: mixWithWhite(color, 0.4), low: mixWithWhite(color, 0.7), unlikely: '#b9b4a8', not_assessed: '#8f8a80' };
  }
  return RAMPS['bilberry'];
}

const BASEMAP_STYLE = 'https://tiles.openfreemap.org/styles/liberty';
const FOREST_WMS =
  'https://avoin.metsakeskus.fi/rajapinnat/v2/stand/ows?service=WMS&version=1.1.1&request=GetMap&layers=v2:stand&styles=&srs=EPSG:3857&bbox={bbox-epsg-3857}&width=256&height=256&format=image/png&transparent=true';
const SOIL_WMS =
  'https://gtkdata.gtk.fi/arcgis/services/Rajapinnat/GTK_Maapera_WMS/MapServer/WMSServer?service=WMS&version=1.1.1&request=GetMap&layers=maapera_200k_maalajit&styles=&srs=EPSG:3857&bbox={bbox-epsg-3857}&width=256&height=256&format=image/png&transparent=true';

interface ViewProp {
  Lat: number;
  Lon: number;
  Zoom: number;
  Key: number;
}

interface RankedArea {
  Id: number;
  Lat: number;
  Lon: number;
  Rank: number;
}

interface UserLocation {
  Lat: number;
  Lon: number;
  Accuracy: number;
}

interface Analysis {
  Pick: { Lat: number; Lon: number } | null;
  RadiusM: number;
  ResultUrl: string | null;
  Mode: string;
  Species: string | null;
  Color: string | null;
  SelectedPinId: string | null;
  Running: boolean;
  FitKey: number;
}

interface Props {
  analysis: Analysis | null;
  locateRequest: number;
  onMapClickId?: string;
  onSelectPinId?: string;
  tileUrl: string;
  species: string;
  color: string | null;
  basemap: string;
  satelliteUrl: string | null;
  showForest: boolean;
  showSoil: boolean;
  view: ViewProp | null;
  selectedAreaId: number | null;
  rankedAreas: RankedArea[];
  userLocation: UserLocation | null;
  pickMode: boolean;
  pickedPoint: { Lat: number; Lon: number } | null;
  coverage: string | null;
  dataVersion: number;
  attribution: string;
  locateLabel: string;
  onViewChangeId?: string;
  onSelectAreaId?: string;
  onSelectStandId?: string;
  onLocateId?: string;
  onPickId?: string;
}

function readProps(props: Record<string, unknown> | undefined): Props {
  const p = props ?? {};
  return {
    analysis: (p['analysis'] as Analysis | null) ?? null,
    locateRequest: (p['locateRequest'] as number) ?? 0,
    onMapClickId: p['onMapClickId'] as string | undefined,
    onSelectPinId: p['onSelectPinId'] as string | undefined,
    tileUrl: (p['tileUrl'] as string) ?? '',
    species: (p['species'] as string) ?? 'bilberry',
    color: (p['color'] as string | null) ?? null,
    basemap: (p['basemap'] as string) ?? 'map',
    satelliteUrl: (p['satelliteUrl'] as string | null) ?? null,
    showForest: Boolean(p['showForest']),
    showSoil: Boolean(p['showSoil']),
    view: (p['view'] as ViewProp | null) ?? null,
    selectedAreaId: (p['selectedAreaId'] as number | null) ?? null,
    rankedAreas: (p['rankedAreas'] as RankedArea[]) ?? [],
    userLocation: (p['userLocation'] as UserLocation | null) ?? null,
    pickMode: Boolean(p['pickMode']),
    pickedPoint: (p['pickedPoint'] as { Lat: number; Lon: number } | null) ?? null,
    coverage: (p['coverage'] as string | null) ?? null,
    dataVersion: (p['dataVersion'] as number) ?? 0,
    attribution: (p['attribution'] as string) ?? '',
    locateLabel: (p['locateLabel'] as string) ?? 'Show my location',
    onViewChangeId: p['onViewChangeId'] as string | undefined,
    onSelectAreaId: p['onSelectAreaId'] as string | undefined,
    onSelectStandId: p['onSelectStandId'] as string | undefined,
    onLocateId: p['onLocateId'] as string | undefined,
    onPickId: p['onPickId'] as string | undefined,
  };
}

function tilesFor(props: Props): string {
  const separator = props.tileUrl.includes('?') ? '&' : '?';
  return props.tileUrl.replace('{species}', props.species) + `${separator}v=${props.dataVersion}`;
}

function suitabilityColor(species: string, color: string | null): maplibregl.ExpressionSpecification {
  const ramp = rampFor(species, color);
  return ['match', ['get', 'suitability'], 'high', ramp.high, 'moderate', ramp.moderate, 'low', ramp.low, 'unlikely', ramp.unlikely, ramp.not_assessed];
}

// ─── Analysis overlays ──────────────────────────────────────────────────

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };
const BERRY_LAYERS = ['stands-fill', 'stands-line', 'areas-fill', 'areas-line', 'areas-selected', 'areas-selected-core'];

// Heatmap ramp over a cell's rank within the search area (0-100): muted grey, soft yellow, vivid green, rich forest green
function heatColor(species: string): maplibregl.ExpressionSpecification {
  const v: maplibregl.ExpressionSpecification = ['coalesce', ['get', `s_${species}`], 0];
  return ['interpolate', ['linear'], v, 0, '#9aa0a6', 50, '#f2e394', 80, '#5cc26f', 95, '#1e7b4f', 100, '#145a39'];
}

function heatOpacity(species: string): maplibregl.ExpressionSpecification {
  const v: maplibregl.ExpressionSpecification = ['coalesce', ['get', `s_${species}`], 0];
  return ['interpolate', ['linear'], v, 0, 0.06, 50, 0.3, 80, 0.55, 95, 0.72];
}

// A geodesic circle (96 points) for the search radius, in [lon, lat]
function circle(lat: number, lon: number, radiusM: number): GeoJSON.Feature {
  const R = 6371008.8;
  const d = radiusM / R;
  const la = (lat * Math.PI) / 180;
  const lo = (lon * Math.PI) / 180;
  const ring: [number, number][] = [];
  for (let k = 0; k <= 96; k++) {
    const b = (2 * Math.PI * k) / 96;
    const la2 = Math.asin(Math.sin(la) * Math.cos(d) + Math.cos(la) * Math.sin(d) * Math.cos(b));
    const lo2 = lo + Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(la), Math.cos(d) - Math.sin(la) * Math.sin(la2));
    ring.push([(lo2 * 180) / Math.PI, (la2 * 180) / Math.PI]);
  }
  return { type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [ring] } };
}

interface AnalysisData {
  cells: GeoJSON.FeatureCollection;
  pins: GeoJSON.FeatureCollection;
}

const analysisData = new WeakMap<MapLibreMap, { url: string; data: AnalysisData | null }>();

function addAnalysisLayers(map: MapLibreMap) {
  map.addSource('analysis-radius', { type: 'geojson', data: EMPTY });
  map.addSource('analysis-cells', { type: 'geojson', data: EMPTY });
  map.addSource('analysis-pins', { type: 'geojson', data: EMPTY, cluster: true, clusterRadius: 42, clusterMaxZoom: 13 });
  map.addLayer({ id: 'analysis-radius-fill', type: 'fill', source: 'analysis-radius', paint: { 'fill-color': '#1e7b4f', 'fill-opacity': 0.06 } });
  map.addLayer({
    id: 'analysis-cells-fill', type: 'fill', source: 'analysis-cells',
    paint: { 'fill-color': '#9aa0a6', 'fill-opacity': 0, 'fill-antialias': false },
  });
  map.addLayer({
    id: 'analysis-radius-line', type: 'line', source: 'analysis-radius',
    paint: { 'line-color': '#1e7b4f', 'line-width': 2, 'line-opacity': 0.85, 'line-dasharray': [2, 1.5] },
  });
  map.addLayer({
    id: 'analysis-clusters', type: 'circle', source: 'analysis-pins', filter: ['has', 'point_count'],
    paint: { 'circle-color': '#1e7b4f', 'circle-radius': ['step', ['get', 'point_count'], 16, 5, 20, 10, 24], 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 3 },
  });
  map.addLayer({
    id: 'analysis-cluster-count', type: 'symbol', source: 'analysis-pins', filter: ['has', 'point_count'],
    layout: { 'text-field': ['get', 'point_count_abbreviated'], 'text-font': ['Noto Sans Bold'], 'text-size': 13 },
    paint: { 'text-color': '#ffffff' },
  });
  map.addLayer({
    id: 'analysis-pin-halo', type: 'circle', source: 'analysis-pins', filter: ['all', ['!', ['has', 'point_count']], ['==', ['get', 'id'], '']],
    paint: { 'circle-radius': 24, 'circle-color': '#ffffff', 'circle-opacity': 0.9, 'circle-blur': 0.2 },
  });
  map.addLayer({
    id: 'analysis-pins', type: 'circle', source: 'analysis-pins', filter: ['!', ['has', 'point_count']],
    paint: { 'circle-radius': 15, 'circle-color': '#1e7b4f', 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 3 },
  });
  map.addLayer({
    id: 'analysis-pin-rank', type: 'symbol', source: 'analysis-pins', filter: ['!', ['has', 'point_count']],
    layout: { 'text-field': ['to-string', ['get', 'rank']], 'text-font': ['Noto Sans Bold'], 'text-size': 13, 'text-allow-overlap': true },
    paint: { 'text-color': '#ffffff' },
  });
}

function applyAnalysis(map: MapLibreMap, p: Props) {
  const a = p.analysis;
  const active = !!a?.Pick;
  for (const layer of BERRY_LAYERS) setVisible(map, layer, !active);

  const radius = map.getSource('analysis-radius') as GeoJSONSource | undefined;
  radius?.setData(active && a?.Pick ? { type: 'FeatureCollection', features: [circle(a.Pick.Lat, a.Pick.Lon, a.RadiusM)] } : EMPTY);
  if (map.getLayer('analysis-radius-line')) {
    map.setPaintProperty('analysis-radius-line', 'line-opacity', a?.Running ? 0.45 : 0.85);
  }

  const species = a?.Species ?? '';
  const color = a?.Color && /^#[0-9a-fA-F]{6}$/.test(a.Color) ? a.Color : '#1e7b4f';
  const heat = active && a?.Mode !== 'pins';
  if (map.getLayer('analysis-cells-fill')) {
    map.setPaintProperty('analysis-cells-fill', 'fill-color', heatColor(species));
    map.setPaintProperty('analysis-cells-fill', 'fill-opacity', heat ? heatOpacity(species) : 0);
  }
  for (const layer of ['analysis-clusters', 'analysis-cluster-count', 'analysis-pins', 'analysis-pin-rank', 'analysis-pin-halo']) {
    setVisible(map, layer, active && a?.Mode === 'pins');
  }
  if (map.getLayer('analysis-pins')) map.setPaintProperty('analysis-pins', 'circle-color', color);
  if (map.getLayer('analysis-clusters')) map.setPaintProperty('analysis-clusters', 'circle-color', color);
  if (map.getLayer('analysis-pin-halo')) map.setFilter('analysis-pin-halo', ['all', ['!', ['has', 'point_count']], ['==', ['get', 'id'], a?.SelectedPinId ?? '']]);

  // Load the result once per URL; superseded loads are dropped
  const url = active ? a?.ResultUrl ?? '' : '';
  const entry = analysisData.get(map);
  if (!entry || entry.url !== url) {
    analysisData.set(map, { url, data: null });
    setAnalysisData(map, null, species);
    if (url) {
      fetch(url)
        .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
        .then((data: AnalysisData) => {
          const current = analysisData.get(map);
          if (!current || current.url !== url) return;
          current.data = data;
          setAnalysisData(map, data, (propsOf.get(map)?.analysis?.Species) ?? species);
        })
        .catch(() => undefined);
    }
  } else if (entry.data) {
    setAnalysisData(map, entry.data, species);
  }
}

const shownSpecies = new WeakMap<MapLibreMap, string>();

function setAnalysisData(map: MapLibreMap, data: AnalysisData | null, species: string) {
  const cells = map.getSource('analysis-cells') as GeoJSONSource | undefined;
  const pins = map.getSource('analysis-pins') as GeoJSONSource | undefined;
  const key = data ? `${species}|${data.cells.features.length}` : '';
  if (shownSpecies.get(map) === key) return;
  shownSpecies.set(map, key);
  cells?.setData(data?.cells ?? EMPTY);
  pins?.setData(data ? { type: 'FeatureCollection', features: data.pins.features.filter((f) => f.properties?.['species'] === species) } : EMPTY);
}

const propsOf = new WeakMap<MapLibreMap, Props>();

function setVisible(map: MapLibreMap, layer: string, visible: boolean) {
  if (map.getLayer(layer)) {
    map.setLayoutProperty(layer, 'visibility', visible ? 'visible' : 'none');
  }
}

const BerryMapRenderer = memo(function BerryMapRenderer({ nodeId, context, className }: UiComponentRendererProps) {
  const node = useUiNode(context.store, nodeId);
  const props = readProps(node?.props as Record<string, unknown> | undefined);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<MapLibreMap | null>(null);
  const propsRef = useRef(props);
  const markersRef = useRef<maplibregl.Marker[]>([]);
  const userMarkerRef = useRef<maplibregl.Marker | null>(null);
  const pickMarkerRef = useRef<maplibregl.Marker | null>(null);
  const lastViewKeyRef = useRef<number>(-1);
  const readyRef = useRef(false);
  const geolocateRef = useRef<maplibregl.GeolocateControl | null>(null);
  const selectionMarkerRef = useRef<maplibregl.Marker | null>(null);
  const lastLocateRef = useRef(props.locateRequest);
  propsRef.current = props;

  // Create the map once
  useEffect(() => {
    if (!containerRef.current) return;
    const initial = propsRef.current.view;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: BASEMAP_STYLE,
      center: initial ? [initial.Lon, initial.Lat] : [25.47, 65.01],
      zoom: initial?.Zoom ?? 10,
      maxBounds: [
        [17.5, 58.5],
        [33.0, 71.0],
      ],
      attributionControl: { compact: true, customAttribution: propsRef.current.attribution },
    });
    mapRef.current = map;
    lastViewKeyRef.current = initial?.Key ?? -1;

    map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-right');
    map.addControl(new maplibregl.ScaleControl({ unit: 'metric' }), 'bottom-left');
    const geolocate = new maplibregl.GeolocateControl({ positionOptions: { enableHighAccuracy: true }, trackUserLocation: false, showAccuracyCircle: true });
    map.addControl(geolocate, 'top-right');
    geolocate.on('geolocate', (event: GeolocationPosition) => {
      const id = propsRef.current.onLocateId;
      if (id) {
        context.dispatchAction(id, JSON.stringify({ lat: event.coords.latitude, lon: event.coords.longitude, accuracy: event.coords.accuracy }));
      }
    });

    map.on('load', () => {
      const p = propsRef.current;
      // Keep the credits one tap away behind the (i) button instead of covering the map on a phone
      const attribution = map.getContainer().querySelector('.maplibregl-ctrl-attrib');
      attribution?.classList.remove('maplibregl-compact-show');
      attribution?.removeAttribute('open');
      map.addSource('satellite', { type: 'raster', tiles: p.satelliteUrl ? [p.satelliteUrl] : [], tileSize: 256, attribution: 'Maanmittauslaitos' });
      map.addLayer({ id: 'satellite', type: 'raster', source: 'satellite', layout: { visibility: 'none' } });
      map.addSource('forest-wms', { type: 'raster', tiles: [FOREST_WMS], tileSize: 256, minzoom: 12, attribution: 'Suomen metsäkeskus' });
      map.addLayer({ id: 'forest-wms', type: 'raster', source: 'forest-wms', minzoom: 12, paint: { 'raster-opacity': 0.75 }, layout: { visibility: 'none' } });
      map.addSource('soil-wms', { type: 'raster', tiles: [SOIL_WMS], tileSize: 256, attribution: 'GTK' });
      map.addLayer({ id: 'soil-wms', type: 'raster', source: 'soil-wms', paint: { 'raster-opacity': 0.6 }, layout: { visibility: 'none' } });

      map.addSource('berry', { type: 'vector', tiles: [tilesFor(p)], minzoom: 8, maxzoom: 15 });
      map.addLayer({
        id: 'stands-fill', type: 'fill', source: 'berry', 'source-layer': 'stands', minzoom: 12.5,
        paint: { 'fill-color': suitabilityColor(p.species, p.color), 'fill-opacity': ['match', ['get', 'suitability'], 'high', 0.55, 'moderate', 0.42, 'low', 0.3, 0.18] },
      });
      map.addLayer({
        id: 'stands-line', type: 'line', source: 'berry', 'source-layer': 'stands', minzoom: 13.5,
        paint: { 'line-color': '#1f2a22', 'line-opacity': 0.25, 'line-width': 0.6 },
      });
      map.addLayer({
        id: 'areas-fill', type: 'fill', source: 'berry', 'source-layer': 'areas', maxzoom: 12.5,
        paint: { 'fill-color': suitabilityColor(p.species, p.color), 'fill-opacity': ['match', ['get', 'suitability'], 'high', 0.7, 0.5] },
      });
      map.addLayer({
        id: 'areas-line', type: 'line', source: 'berry', 'source-layer': 'areas',
        paint: { 'line-color': suitabilityColor(p.species, p.color), 'line-width': ['interpolate', ['linear'], ['zoom'], 9, 0.5, 14, 2], 'line-opacity': 0.9 },
      });
      map.addLayer({
        id: 'areas-selected', type: 'line', source: 'berry', 'source-layer': 'areas',
        filter: ['==', ['get', 'id'], p.selectedAreaId ?? -1],
        paint: { 'line-color': '#f5f1e6', 'line-width': 4, 'line-opacity': 1 },
      });
      map.addLayer({
        id: 'areas-selected-core', type: 'line', source: 'berry', 'source-layer': 'areas',
        filter: ['==', ['get', 'id'], p.selectedAreaId ?? -1],
        paint: { 'line-color': '#14261b', 'line-width': 2 },
      });

      map.addSource('coverage', { type: 'geojson', data: p.coverage ? JSON.parse(p.coverage) : { type: 'FeatureCollection', features: [] } });
      map.addLayer({ id: 'coverage-line', type: 'line', source: 'coverage', paint: { 'line-color': '#2f5d3a', 'line-width': 1.5, 'line-dasharray': [3, 2] } });
      addAnalysisLayers(map);

      readyRef.current = true;
      // The vector source was created with the current tiles, so applyOverlays must not reload them straight away
      appliedKeys.set(map, `${p.species}|${p.dataVersion}|${p.tileUrl}`);
      applyOverlays(map, propsRef.current);
    });

    const reportView = () => {
      const id = propsRef.current.onViewChangeId;
      if (!id) return;
      const b = map.getBounds();
      const c = map.getCenter();
      context.dispatchAction(id, JSON.stringify({ west: b.getWest(), south: b.getSouth(), east: b.getEast(), north: b.getNorth(), lat: c.lat, lon: c.lng, zoom: map.getZoom() }));
    };
    map.on('moveend', reportView);
    map.on('idle', function firstIdle() {
      map.off('idle', firstIdle);
      reportView();
    });

    map.on('click', (event) => {
      const p = propsRef.current;
      if (p.pickMode) {
        if (p.onPickId) context.dispatchAction(p.onPickId, JSON.stringify({ lat: event.lngLat.lat, lon: event.lngLat.lng }));
        return;
      }

      // Analysis pins and clusters first; then any other tap chooses that point for a new analysis
      const pinHits = map.queryRenderedFeatures(event.point, { layers: ['analysis-pins', 'analysis-clusters'].filter((l) => map.getLayer(l)) });
      const pin = pinHits.find((f) => f.layer.id === 'analysis-pins');
      if (pin && p.onSelectPinId) {
        context.dispatchAction(p.onSelectPinId, String(pin.properties?.['id']));
        return;
      }
      const cluster = pinHits.find((f) => f.layer.id === 'analysis-clusters');
      if (cluster) {
        const source = map.getSource('analysis-pins') as GeoJSONSource;
        void source.getClusterExpansionZoom(cluster.properties?.['cluster_id'] as number).then((zoom) => {
          map.easeTo({ center: (cluster.geometry as GeoJSON.Point).coordinates as [number, number], zoom });
        });
        return;
      }
      if (p.onMapClickId) {
        context.dispatchAction(p.onMapClickId, JSON.stringify({ lat: event.lngLat.lat, lon: event.lngLat.lng }));
        return;
      }

      const hits: MapGeoJSONFeature[] = map.queryRenderedFeatures(event.point, { layers: ['areas-fill', 'areas-line', 'stands-fill'].filter((l) => map.getLayer(l)) });
      const area = hits.find((f) => f.sourceLayer === 'areas');
      if (area && p.onSelectAreaId) {
        context.dispatchAction(p.onSelectAreaId, String(area.properties?.['id']));
        return;
      }

      const stand = hits.find((f) => f.sourceLayer === 'stands');
      if (stand && p.onSelectStandId) {
        context.dispatchAction(p.onSelectStandId, String(stand.properties?.['id']));
      }
    });

    for (const layer of ['areas-fill', 'stands-fill', 'analysis-pins', 'analysis-clusters']) {
      map.on('mouseenter', layer, () => (map.getCanvas().style.cursor = 'pointer'));
      map.on('mouseleave', layer, () => (map.getCanvas().style.cursor = ''));
    }

    geolocateRef.current = geolocate;
    const resize = new ResizeObserver(() => map.resize());
    resize.observe(containerRef.current);

    return () => {
      resize.disconnect();
      readyRef.current = false;
      map.remove();
      mapRef.current = null;
    };
  }, [context]);

  // Keep layers in step with props
  useEffect(() => {
    const map = mapRef.current;
    if (map && readyRef.current) applyOverlays(map, props);
  });

  // Fly when C# asks for a new view (search result, list click), never on ordinary re-renders
  useEffect(() => {
    const map = mapRef.current;
    const view = props.view;
    if (!map || !view || view.Key === lastViewKeyRef.current) return;
    lastViewKeyRef.current = view.Key;
    // Centre the target in the part of the map the panel (desktop) or sheet (phone) leaves visible
    const el = map.getContainer();
    const phone = el.clientWidth < 768;
    const padding = phone ? { top: 80, bottom: Math.round(el.clientHeight * 0.5), left: 0, right: 0 } : { top: 0, bottom: 0, left: 430, right: 0 };
    map.flyTo({ center: [view.Lon, view.Lat], zoom: view.Zoom, speed: 1.6, padding });
  }, [props.view]);

  // Numbered markers for the ranked results the list shows
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const cleanups: (() => void)[] = [];
    markersRef.current = props.rankedAreas.slice(0, 20).map((area) => {
      const el = document.createElement('button');
      el.type = 'button';
      el.className = 'berry-rank-marker' + (area.Id === props.selectedAreaId ? ' is-selected' : '');
      el.textContent = String(area.Rank);
      el.setAttribute('aria-label', `Area ${area.Rank}`);
      const onClick = (e: MouseEvent) => {
        e.stopPropagation();
        const id = propsRef.current.onSelectAreaId;
        if (id) context.dispatchAction(id, String(area.Id));
      };
      // eslint-disable-next-line @eslint-react/web-api-no-leaked-event-listener -- removed by the cleanup below
      el.addEventListener('click', onClick);
      cleanups.push(() => el.removeEventListener('click', onClick));
      return new maplibregl.Marker({ element: el }).setLngLat([area.Lon, area.Lat]).addTo(map);
    });
    return () => {
      cleanups.forEach((c) => c());
      markersRef.current.forEach((m) => m.remove());
      markersRef.current = [];
    };
  }, [props.rankedAreas, props.selectedAreaId, context]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    userMarkerRef.current?.remove();
    userMarkerRef.current = null;
    if (props.userLocation) {
      const el = document.createElement('div');
      el.className = 'berry-user-marker';
      userMarkerRef.current = new maplibregl.Marker({ element: el }).setLngLat([props.userLocation.Lon, props.userLocation.Lat]).addTo(map);
    }
  }, [props.userLocation]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    pickMarkerRef.current?.remove();
    pickMarkerRef.current = null;
    if (props.pickedPoint) {
      pickMarkerRef.current = new maplibregl.Marker({ color: '#7b2e7d' }).setLngLat([props.pickedPoint.Lon, props.pickedPoint.Lat]).addTo(map);
    }
    map.getCanvas().style.cursor = props.pickMode ? 'crosshair' : '';
  }, [props.pickedPoint, props.pickMode]);

  // The chosen point: a pin that drops in, with a soft pulse while the model works
  const pick = props.analysis?.Pick ?? null;
  const running = props.analysis?.Running ?? false;
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    selectionMarkerRef.current?.remove();
    selectionMarkerRef.current = null;
    if (!pick) return;
    const el = document.createElement('div');
    el.className = 'berry-selection-marker' + (running ? ' is-running' : '');
    el.innerHTML = '<span class="pulse"></span><span class="pin"></span>';
    selectionMarkerRef.current = new maplibregl.Marker({ element: el, anchor: 'bottom' }).setLngLat([pick.Lon, pick.Lat]).addTo(map);
    // eslint-disable-next-line @eslint-react/exhaustive-deps -- the marker follows the coordinates, not the object's identity
  }, [pick?.Lat, pick?.Lon, running]);

  // Frame the search circle in the part of the map the panel (desktop) or the sheet (phone) leaves visible
  const fitKey = props.analysis?.FitKey ?? 0;
  const lastFitRef = useRef(fitKey);
  useEffect(() => {
    const map = mapRef.current;
    const a = propsRef.current.analysis;
    if (!map || fitKey === lastFitRef.current || !a?.Pick) return;
    lastFitRef.current = fitKey;
    const ring = (circle(a.Pick.Lat, a.Pick.Lon, a.RadiusM).geometry as GeoJSON.Polygon).coordinates[0];
    const lons = ring.map((c) => c[0]);
    const lats = ring.map((c) => c[1]);
    const el = map.getContainer();
    const phone = el.clientWidth < 768;
    const padding = phone
      ? { top: 90, left: 24, right: 24, bottom: Math.round(el.clientHeight * 0.5) }
      : { top: 40, left: 450, right: 70, bottom: 40 };
    map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding, duration: 900 });
  }, [fitKey]);

  // "Use my location" in the panel asks the map's own locate control
  useEffect(() => {
    if (props.locateRequest !== lastLocateRef.current) {
      lastLocateRef.current = props.locateRequest;
      geolocateRef.current?.trigger();
    }
  }, [props.locateRequest]);

  if (!node) return null;
  return <div ref={containerRef} className={`berry-map ${className ?? ''}`} />;
});

const appliedKeys = new WeakMap<MapLibreMap, string>();

function applyOverlays(map: MapLibreMap, p: Props) {
  propsOf.set(map, p);
  applyAnalysis(map, p);
  setVisible(map, 'satellite', p.basemap === 'satellite' && !!p.satelliteUrl);
  setVisible(map, 'forest-wms', p.showForest);
  setVisible(map, 'soil-wms', p.showSoil);

  const key = `${p.species}|${p.dataVersion}|${p.tileUrl}`;
  if (appliedKeys.get(map) !== key) {
    appliedKeys.set(map, key);
    const source = map.getSource('berry') as maplibregl.VectorTileSource | undefined;
    source?.setTiles([tilesFor(p)]);
    for (const layer of ['stands-fill', 'areas-fill']) {
      if (map.getLayer(layer)) map.setPaintProperty(layer, 'fill-color', suitabilityColor(p.species, p.color));
    }
    if (map.getLayer('areas-line')) map.setPaintProperty('areas-line', 'line-color', suitabilityColor(p.species, p.color));
  }

  for (const layer of ['areas-selected', 'areas-selected-core']) {
    if (map.getLayer(layer)) map.setFilter(layer, ['==', ['get', 'id'], p.selectedAreaId ?? -1]);
  }

  const coverage = map.getSource('coverage') as GeoJSONSource | undefined;
  if (coverage && p.coverage) coverage.setData(JSON.parse(p.coverage));
}

export function createBerryMapResolver(): IkonUiComponentResolver {
  return (node) => (node.type === 'berry-map' ? BerryMapRenderer : undefined);
}
