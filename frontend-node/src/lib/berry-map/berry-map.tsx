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

interface Props {
  tileUrl: string;
  species: string;
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
    tileUrl: (p['tileUrl'] as string) ?? '',
    species: (p['species'] as string) ?? 'bilberry',
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

function suitabilityColor(species: string): maplibregl.ExpressionSpecification {
  const ramp = RAMPS[species] ?? RAMPS['bilberry'];
  return ['match', ['get', 'suitability'], 'high', ramp.high, 'moderate', ramp.moderate, 'low', ramp.low, 'unlikely', ramp.unlikely, ramp.not_assessed];
}

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
      map.addSource('satellite', { type: 'raster', tiles: p.satelliteUrl ? [p.satelliteUrl] : [], tileSize: 256, attribution: 'Maanmittauslaitos' });
      map.addLayer({ id: 'satellite', type: 'raster', source: 'satellite', layout: { visibility: 'none' } });
      map.addSource('forest-wms', { type: 'raster', tiles: [FOREST_WMS], tileSize: 256, minzoom: 12, attribution: 'Suomen metsäkeskus' });
      map.addLayer({ id: 'forest-wms', type: 'raster', source: 'forest-wms', minzoom: 12, paint: { 'raster-opacity': 0.75 }, layout: { visibility: 'none' } });
      map.addSource('soil-wms', { type: 'raster', tiles: [SOIL_WMS], tileSize: 256, attribution: 'GTK' });
      map.addLayer({ id: 'soil-wms', type: 'raster', source: 'soil-wms', paint: { 'raster-opacity': 0.6 }, layout: { visibility: 'none' } });

      map.addSource('berry', { type: 'vector', tiles: [tilesFor(p)], minzoom: 8, maxzoom: 15 });
      map.addLayer({
        id: 'stands-fill', type: 'fill', source: 'berry', 'source-layer': 'stands', minzoom: 12.5,
        paint: { 'fill-color': suitabilityColor(p.species), 'fill-opacity': ['match', ['get', 'suitability'], 'high', 0.55, 'moderate', 0.42, 'low', 0.3, 0.18] },
      });
      map.addLayer({
        id: 'stands-line', type: 'line', source: 'berry', 'source-layer': 'stands', minzoom: 13.5,
        paint: { 'line-color': '#1f2a22', 'line-opacity': 0.25, 'line-width': 0.6 },
      });
      map.addLayer({
        id: 'areas-fill', type: 'fill', source: 'berry', 'source-layer': 'areas', maxzoom: 12.5,
        paint: { 'fill-color': suitabilityColor(p.species), 'fill-opacity': ['match', ['get', 'suitability'], 'high', 0.7, 0.5] },
      });
      map.addLayer({
        id: 'areas-line', type: 'line', source: 'berry', 'source-layer': 'areas',
        paint: { 'line-color': suitabilityColor(p.species), 'line-width': ['interpolate', ['linear'], ['zoom'], 9, 0.5, 14, 2], 'line-opacity': 0.9 },
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

    for (const layer of ['areas-fill', 'stands-fill']) {
      map.on('mouseenter', layer, () => (map.getCanvas().style.cursor = 'pointer'));
      map.on('mouseleave', layer, () => (map.getCanvas().style.cursor = ''));
    }

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
    map.flyTo({ center: [view.Lon, view.Lat], zoom: view.Zoom, speed: 1.6 });
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

  if (!node) return null;
  return <div ref={containerRef} className={`berry-map ${className ?? ''}`} />;
});

const appliedKeys = new WeakMap<MapLibreMap, string>();

function applyOverlays(map: MapLibreMap, p: Props) {
  setVisible(map, 'satellite', p.basemap === 'satellite' && !!p.satelliteUrl);
  setVisible(map, 'forest-wms', p.showForest);
  setVisible(map, 'soil-wms', p.showSoil);

  const key = `${p.species}|${p.dataVersion}|${p.tileUrl}`;
  if (appliedKeys.get(map) !== key) {
    appliedKeys.set(map, key);
    const source = map.getSource('berry') as maplibregl.VectorTileSource | undefined;
    source?.setTiles([tilesFor(p)]);
    for (const layer of ['stands-fill', 'areas-fill']) {
      if (map.getLayer(layer)) map.setPaintProperty(layer, 'fill-color', suitabilityColor(p.species));
    }
    if (map.getLayer('areas-line')) map.setPaintProperty('areas-line', 'line-color', suitabilityColor(p.species));
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
