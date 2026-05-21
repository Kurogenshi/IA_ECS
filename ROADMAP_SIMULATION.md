# Roadmap — Évolution vers une vraie simulation de foule

Ce document est le **plan de travail forward** du projet. Il complète `RAPPORT_DEVELOPPEMENT_IA.md` (qui documente l'historique de ce qui a été fait) en listant **ce qu'il reste à faire**, phase par phase, avec assez de détails techniques pour qu'une nouvelle session Claude (ou toi seul) puisse reprendre exactement où on s'est arrêté.

## Convention de statut

Chaque phase porte un statut à mettre à jour au fil de l'eau :

- `[ ] TODO` — pas commencé
- `[~] EN COURS` — démarré mais non livré
- `[x] FAIT` — livré et validé en jeu (date + commit hash)

Quand une phase est terminée :
1. Coche-la (`[x] FAIT — <date> — commit <hash>`)
2. Ajoute une ligne **"Livré"** sous "Critère de validation" résumant ce qui marche concrètement
3. Note d'éventuels écarts par rapport au plan initial (composants renommés, choix d'implémentation différents, etc.)

## Vue d'ensemble des phases

| #  | Phase                                              | Statut       | Dépend de |
|----|----------------------------------------------------|--------------|-----------|
| 1  | Obstacles statiques & zones interdites             | [x] FAIT — 2026-05-20 | —         |
| 2  | Zones marchables explicites (navmesh light)        | [x] FAIT — 2026-05-20 | 1         |
| 3  | Local avoidance (ORCA-lite) + behavior dynamics    | [x] FAIT — 2026-05-20 | 1         |
| 4  | Points d'intérêt & comportements à but             | [x] FAIT — 2026-05-20 | 2         |
| 5  | Pathfinding A* sur grille                          | [ ] TODO     | 1, 2      |
| 6  | Interactions sociales (groupes, rencontres)        | [ ] TODO     | 4         |
| 7  | Cycle jour/nuit & schedules                        | [ ] TODO     | 4         |
| 8  | Spawn/despawn aux bords (cycle de vie)             | [ ] TODO     | 4         |
| 9  | Perception & réactions à événements                | [ ] TODO     | 4         |
| 10 | Debug tooling (heatmap, vector field, inspecteur)  | [ ] TODO     | 1         |
| 11 | Infrastructure routière (roads & crosswalks)       | [ ] TODO     | 2         |
| 12 | Voitures : entités, voies, conduite de base        | [ ] TODO     | 11        |
| 13 | IA trafic : voiture ↔ voiture                      | [ ] TODO     | 12        |
| 14 | Symbiose piétons ↔ voitures                        | [ ] TODO     | 4, 11, 12 |
| 15 | Diversité véhiculaire (bus, vélos, urgence)        | [ ] TODO     | 14        |

**Ordre recommandé** :
- **Piéton-only** : 1 → 2 → 3 → 4 → puis 5/6/7/8/9 dans l'ordre qui te plaît (indépendantes entre elles), 10 quand le besoin se fait sentir.
- **Branche véhicules** : 11 (infrastructure, indépendant) → 12 → 13 → 14. Peut être lancée en parallèle de la branche piéton à partir de Phase 4 livrée. Phase 14 (symbiose) nécessite que Phase 4 soit là pour que les piétons aient des destinations claires côté crosswalks.
- **Phase 15** : optionnelle, peut être tirée plus tard selon le besoin (bus liée à Phase 4 "POI BusStop", urgence liée à Phase 9 "stimulus").

---

## État actuel du code (point de départ)

Pour qu'une nouvelle session puisse se situer sans relire tout le code :

### Architecture en place

```
Assets/Scripts/
├── Components/
│   ├── AgentComponents.cs            (AgentTag, AgentMovement, AgentTypeData, PathFollower, Waypoint, SpawnerConfig, SpawnerPathRef)
│   └── AgentAnimationComponents.cs   (AgentAnimationState, VATClipTable, AnimClipProperty, AnimTimeProperty, AgentVisibleProperty, AgentShadowVisibleProperty)
├── Authoring/
│   ├── AgentAuthoring.cs             (prefab agent → entité)
│   ├── PathAuthoring.cs              (waypoints sur trottoirs)
│   └── CrowdSpawnerAuthoring.cs      (spawner global, expose tous les paramètres)
├── Systems/
│   ├── CrowdSpawnerSystem.cs         (one-shot spawn en grille jitterée, désactive ensuite)
│   ├── AgentSteeringSystem.cs        (spatial hash + path follow + séparation)
│   ├── AgentMovementSystem.cs        (applique vélocité au transform, force y=0)
│   ├── AgentAnimationSystem.cs       (Idle/Walk selon vitesse, écrit dans les MaterialProperty)
│   ├── AgentVisibilitySystem.cs      (cull per-instance distance caméra)
│   ├── AgentLODBakingSystem.cs       (propage MaterialProperty aux enfants LOD au baking)
│   └── PropagateMaterialPropsToLODSystem.cs (mirroir runtime root → LOD children)
├── Animation/
│   └── VATAsset.cs                   (ScriptableObject : texture VAT + clips)
├── Editor/
│   ├── ClusterDecimator.cs           (décimation mesh pour LOD)
│   └── VATBakerWindow.cs             (bake mesh + texture VAT par LOD)
└── UI/
    └── CrowdHUD.cs                   (FPS + agent count, OnGUI)
```

### Limitations actuelles à lever (ce que la roadmap adresse)

- **Aucun obstacle statique** : un agent traverse un bâtiment sans s'en rendre compte (Phase 1).
- **Pas de notion de zone marchable** : c'est le placement manuel des waypoints sur les trottoirs qui contraint le mouvement, pas une donnée structurée que l'agent peut interroger (Phase 2).
- **Séparation purement réactive** : pas d'anticipation, deux flux opposés se bloquent (Phase 3).
- **Boucle de waypoints infinie** : l'agent n'a pas de destination ni de motivation (Phase 4).
- **Pas de pathfinding** : un agent doit suivre un chemin pré-tracé, il ne peut pas calculer un trajet A→B (Phase 5).
- **Pas d'interactions sociales** : les agents s'évitent comme des particules, pas comme des humains (Phase 6).
- **Population figée** : le spawner instancie tout au démarrage et se désactive (Phases 7, 8).
- **Aucune réaction à des événements externes** (Phase 9).
- **Difficile de diagnostiquer** ce qui se passe à grande échelle (Phase 10).

### Conventions à respecter dans tout nouveau code

- **Tous les composants runtime sont des `IComponentData` unmanaged** (struct + types blittables uniquement). Pas de `IComponentData` classes.
- **Tous les jobs sont `[BurstCompile]`**. Pas d'API managées (string, ref types) dans les jobs.
- **Pas de `Position.y` simulé** pour l'instant : la scène est sur un plan, on force `y=0` après chaque mouvement (cf. `AgentMovementSystem.cs:37`). Ce sera à revoir si un jour on veut des escaliers / terrain en pente — pas dans la roadmap actuelle.
- **Singletons via `SpawnerConfig`** quand un paramètre est global. Préférer ajouter un champ ici qu'un nouveau singleton.
- **Authoring → Baker → ECS** : tout nouveau type de données scène (obstacle, zone, POI) passe par un MonoBehaviour Authoring avec gizmo de visualisation.

---

# Phases détaillées

---

## Phase 1 — Obstacles statiques & zones interdites

**Statut : `[x] FAIT — 2026-05-20`**

### But fonctionnel

Empêcher physiquement les agents de pénétrer dans les bâtiments, sur la route, dans les zones marquées comme interdites. C'est la **fondation de toute la roadmap** : sans ça, on ne peut ni faire du pathfinding (Phase 5), ni de la perception (Phase 9), ni un placement de POI réaliste (Phase 4).

### Composants & données à créer

**Nouveau fichier `Assets/Scripts/Components/ObstacleComponents.cs`** :

```csharp
public enum ObstacleShape : byte { Box = 0, Circle = 1 }

public struct StaticObstacle : IBufferElementData
{
    public ObstacleShape Shape;
    public float3 Center;       // monde
    public float3 HalfExtents;  // Box: x/z half size, y ignoré pour l'instant. Circle: x = rayon
    public float  RotationY;    // radians, pour Box
}

// Tag sur l'entité singleton qui porte le buffer
public struct ObstacleWorld : IComponentData { }
```

Stocker tous les obstacles dans un **buffer unique sur une entité singleton** (`ObstacleWorld`) — c'est plus simple à lire dans les jobs qu'un buffer par obstacle.

### Authoring

**Nouveau fichier `Assets/Scripts/Authoring/ObstacleAuthoring.cs`** :
- `MonoBehaviour` qu'on pose sur un GameObject (bâtiment, barrière, zone interdite).
- Champ `Shape` (enum), `HalfExtents` (Vector3).
- `OnDrawGizmos` qui dessine un wireframe rouge (Box) ou cercle (Circle).
- Le Baker **n'instancie pas une entité par obstacle** ; il enregistre la donnée dans une liste statique qui sera consommée par un Baking System.

**Nouveau fichier `Assets/Scripts/Authoring/ObstacleWorldBaker.cs`** (BakingSystem ECS) :
- S'exécute après tous les `ObstacleAuthoring` bakers.
- Crée une entité singleton avec le tag `ObstacleWorld` + un `DynamicBuffer<StaticObstacle>` contenant tous les obstacles bakés.

### Système runtime de répulsion

Modifier `AgentSteeringSystem.cs` :

1. **Au démarrage du job de steering**, ajouter un lookup vers le buffer d'obstacles :
   ```csharp
   var obstacleBuffer = SystemAPI.GetSingletonBuffer<StaticObstacle>(true);
   ```
   Le passer en `[ReadOnly] public NativeArray<StaticObstacle> Obstacles` au job (via `.AsNativeArray()`).

2. **Optimisation** : construire une grille spatiale des obstacles (clé = cellule monde, valeur = index dans le tableau). À faire **une seule fois** au boot (les obstacles sont statiques) → un autre singleton `ObstacleSpatialIndex` créé par `ObstacleWorldBaker` ou un système one-shot.

3. **Dans `SteeringJob.Execute`**, après le calcul de séparation entre agents, ajouter une **force de répulsion d'obstacles** :
   - Itérer les obstacles de la cellule courante + 8 voisines (rayon de répulsion = 2-3m).
   - Pour chaque obstacle, calculer le point le plus proche sur l'AABB/cercle.
   - Si distance < `ObstacleRepulsionRadius`, ajouter une force inversement proportionnelle à la distance, pondérée fort (`obstacleWeight ≈ 4.0`, plus que la séparation entre agents).

4. **Combinaison finale** :
   ```
   steer = desired * pathWeight + separation * sepWeight + obstacleRepulsion * obstacleWeight
   ```

### Résolution de pénétration (filet de sécurité)

Dans `AgentMovementSystem.MovementJob.Execute`, après `transform.Position += movement.Velocity * DeltaTime` :
- Vérifier si la nouvelle position est **à l'intérieur** d'un obstacle.
- Si oui, projeter la position vers le bord le plus proche + appliquer un petit offset (0.05m).
- Couper la composante normale de la vélocité (sliding le long du mur).

Cette deuxième passe est volontairement simple et indépendante de la prédiction du steering — elle garantit qu'on **ne traverse jamais** un mur même en cas de bug ou de gros pas de temps.

### Paramètres à ajouter à `SpawnerConfig`

```csharp
public float ObstacleRepulsionRadius;   // 2.5f par défaut
public float ObstacleWeight;            // 4.0f par défaut
public float ObstacleCellSize;          // 4f par défaut, grille spatiale obstacles
```

Et exposer dans `CrowdSpawnerAuthoring.cs` (section "Obstacle Avoidance").

### Critère de validation

- [ ] Spawn 5000 agents au milieu d'un cube `ObstacleAuthoring` → tous sortent dans les 2 secondes.
- [ ] Aucun agent ne traverse un bâtiment quand il marche le long d'un trottoir adjacent.
- [ ] Le framerate ne perd pas plus de 5% par rapport à avant (avec ~50 obstacles dans la scène).

### Notes d'implémentation pour reprise

- La grille spatiale obstacles **réutilise les utilitaires** `SpatialHashUtil` de `AgentSteeringSystem.cs:233`. Considère factoriser si on duplique.
- Penser à un `[UpdateInGroup(typeof(BakingSystemGroup))]` sur `ObstacleWorldBaker` pour qu'il tourne au bon moment.
- Si la scène a > 500 obstacles, profile : la construction de la grille au boot peut devenir longue.

### État de l'implémentation (à valider en Unity)

**Code livré le 2026-05-20** — en attente de validation visuelle dans l'éditeur avant de basculer le statut en `[x] FAIT`.

**Écarts par rapport au plan initial** :

- **Pas de buffer singleton + pas de BakingSystem d'agrégation.** À la place, chaque `ObstacleAuthoring` baked produit **une entité par obstacle** avec un `StaticObstacle` *en IComponentData* (et non IBufferElementData). Au runtime, `ObstacleSpatialIndexSystem` (one-shot dans `InitializationSystemGroup`) collecte toutes ces entités via une query, les copie dans une `NativeArray<StaticObstacle>` persistante, et bâtit la `NativeParallelMultiHashMap<int, int>` (cell hash → index dans le NativeArray). Les deux containers sont stockés dans le singleton `ObstacleSpatialIndex`. Plus simple, et la singleton tag `ObstacleWorld` n'est donc pas implémentée — `ObstacleSpatialIndex` joue le rôle de singleton.
- **Le système d'index crée toujours le singleton, même sans aucun obstacle** (NativeArray de taille 0). Permet aux systèmes downstream de `RequireForUpdate<ObstacleSpatialIndex>()` sans s'inquiéter du cas "scène sans obstacles".
- **Une seule passe `MovementJob`** intègre la vélocité + fait le pushout (au lieu de deux jobs séparés). Le pushout fait deux itérations consécutives pour gérer les coins où sortir d'un obstacle nous pousse dans un autre.
- **Le `MovementSystem` ne tient plus à `SpawnerConfig` pour le cell size obstacle** — il lit `ObstacleSpatialIndex.CellSize` directement, ce qui garantit cohérence avec ce que l'index a réellement utilisé au build.

**Fichiers créés** :
- `Assets/Scripts/Components/ObstacleComponents.cs` : `ObstacleShape`, `StaticObstacle`, `ObstacleSpatialIndex`, `ObstacleMath` (closest point + AABB).
- `Assets/Scripts/Authoring/ObstacleAuthoring.cs` : MonoBehaviour + Baker + gizmos Box/Circle.
- `Assets/Scripts/Systems/ObstacleSpatialIndexSystem.cs` : one-shot, build au boot.

**Fichiers modifiés** :
- `Assets/Scripts/Components/AgentComponents.cs` : 3 champs ajoutés à `SpawnerConfig` (`ObstacleRepulsionRadius`, `ObstacleWeight`, `ObstacleCellSize`). Champ `StuckTimer` ajouté à `AgentMovement` (cf. fix wall-sliding).
- `Assets/Scripts/Authoring/CrowdSpawnerAuthoring.cs` : section *Static Obstacles (Phase 1)* dans l'Inspector + écriture dans le Baker.
- `Assets/Scripts/Systems/AgentSteeringSystem.cs` : `RequireForUpdate<ObstacleSpatialIndex>`, force de répulsion quadratique injectée dans le steering blend, poids configurable. **Wall-sliding** : projection tangentielle du vecteur `desired` quand il pointe dans un obstacle proche (rayon = 1.5 × repulsion radius). **Stuck detection** : si la vitesse cible reste < 20% de la vitesse de croisière pendant > 2s sur un chemin, l'agent saute au waypoint suivant.
- `Assets/Scripts/Systems/AgentMovementSystem.cs` : pushout 2 passes après intégration de vélocité, slide tangent au mur.

**Itération 2 (fix "agents bloqués face au mur")** :
Première version livrée : les agents étaient correctement éjectés des obstacles, mais quand leur waypoint était de l'autre côté d'un mur, ils continuaient d'essayer de marcher dedans → blocage. Deux mécanismes ajoutés :
1. **Wall-sliding** : dans la boucle d'obstacles du steering, si `desired` a une composante vers l'intérieur du mur, on la retire (projection sur la tangente, pondérée par la distance). Résultat : `desired` longe le mur dans la direction qui rapproche le plus du waypoint.
2. **Skip-waypoint d'urgence** : `AgentMovement.StuckTimer` accumule le temps passé en quasi-stationnaire avec un chemin actif. Au-delà de 2s, on incrémente `PathFollower.CurrentWaypoint`. Permet aux agents dont la cible est inaccessible (waypoint enfermé par un obstacle) de se débloquer en attendant Phase 5 (pathfinding A*).

**Procédure de validation à exécuter par l'utilisateur** :

1. Ouvrir la scène CrowdSubScene dans Unity.
2. Créer un GameObject vide, lui ajouter `ObstacleAuthoring`, lui donner `Shape = Box`, `HalfExtents = (3, 1, 3)`. Le poser au milieu de la zone de spawn.
3. Play. Les agents qui spawnent dedans doivent **en sortir dans les 2 secondes**.
4. Tester avec une rotation Y non nulle sur l'obstacle (ex: 30°) → l'AABB doit se comporter en rotated box.
5. Tester avec `Shape = Circle` → même comportement.
6. Tester en plaçant un obstacle sur le trajet d'un path : les agents doivent contourner sans s'arrêter.
7. Vérifier dans le HUD que le framerate ne s'effondre pas (perte < 5% pour ~50 obstacles).
8. Si OK : passer le statut à `[x] FAIT — 2026-05-20 — commit <hash>` dans la table en haut **et** dans le titre de cette phase, puis copier ce bloc "État de l'implémentation" dans une nouvelle section **"Livré"**.

---

## Phase 2 — Zones marchables explicites (navmesh light)

**Statut : `[x] FAIT — 2026-05-20`**

### But fonctionnel

Aujourd'hui, c'est uniquement la position des waypoints (placés sur les trottoirs à la main) qui contraint les agents à rester sur les trottoirs. Si un agent dévie (séparation forte, répulsion d'obstacle), rien ne le ramène. On veut une notion **explicite** de "ici, l'agent a le droit de marcher".

### Composants & données

**Compléter `Assets/Scripts/Components/ObstacleComponents.cs`** :

```csharp
public struct WalkableArea : IBufferElementData
{
    public float3 Center;
    public float3 HalfExtents;
    public float  RotationY;
    // V1 = AABB orientée. V2 plus tard = polygone convexe.
}

public struct WalkableWorld : IComponentData { }
```

### Authoring

**Nouveau `Assets/Scripts/Authoring/WalkableAreaAuthoring.cs`** :
- Symétrique à `ObstacleAuthoring`, gizmo vert clair.
- Pose sur chaque trottoir, place, plaza.
- Baker similaire : un `WalkableWorldBaker` agrège tout dans un buffer singleton.

### Système runtime

**Stratégie** : au lieu d'un nouveau système, **modifier `AgentMovementSystem`** pour faire un test "la position résultante est-elle dans au moins une `WalkableArea` ?". Si non, projeter la position vers le bord le plus proche d'une zone walkable.

Pour rester rapide :
- Construire au boot une grille spatiale "quelles zones walkable couvrent cette cellule ?" (similaire à la grille d'obstacles Phase 1).
- Dans `MovementJob`, après application de la vélocité (et avant/après résolution d'obstacle de Phase 1), interroger la grille walkable.
- Si la position est hors de toute zone marchable → recule l'agent vers le bord le plus proche, coupe la composante normale de la vélocité.

### Cas particulier : transition entre zones

Deux trottoirs adjacents (intersection) doivent **se chevaucher légèrement** dans l'authoring pour éviter qu'un agent à la frontière "tombe entre les deux". Documenter ça dans le tooltip du `WalkableAreaAuthoring`.

### Critère de validation

- [ ] Un agent stationnaire qui wander reste sur le trottoir (ne déborde pas sur la route).
- [ ] Un agent poussé par séparation contre le bord d'un trottoir ne traverse pas la route.
- [ ] Aux intersections (zones qui se chevauchent), pas de "saut" visuel.

### Notes pour reprise

- Si la perf est limite, faire une grille **bitmask** plutôt qu'une liste : 1 bit par zone par cellule, max 32 ou 64 zones par cellule.
- Phase 5 (pathfinding) **dépend de cette donnée** : la grille A* sera dérivée de l'union des `WalkableArea` moins les `StaticObstacle`.

### État de l'implémentation (à valider en Unity)

**Code livré le 2026-05-20** — en attente de validation visuelle avant bascule en `[x] FAIT`.

**Écarts par rapport au plan initial** :

- **Contrainte appliquée uniquement dans `AgentMovementSystem`**, pas dans `AgentSteeringSystem`. Le roadmap suggérait aussi de rabattre la vélocité tangentiellement dans le steering ; pour Phase 2 v1 on s'est contenté de la passe corrective dans le mouvement. Si du jitter apparaît à grande échelle contre les bords, on ajoutera la projection tangentielle dans le steering (même pattern que le wall-sliding obstacles).
- **Flag `HasAreas` sur le singleton** : si aucun `WalkableArea` n'est baked, la contrainte est totalement désactivée. Permet de tester la Phase 1 sans définir de trottoirs au préalable, et ne casse pas les scènes pré-Phase-2.
- **Réutilisation de l'enum `ObstacleShape`** et de la math de `ObstacleMath` (refactorée pour exposer des overloads sur primitives `ClosestPointOnShape` et `WorldAABBOfShape`). Évite la duplication de code géométrique entre obstacles et zones marchables.
- **Ordre d'application des contraintes dans `MovementJob`** : intégration vélocité → pushout obstacles ×2 → snap walkable → pushout obstacles ×1. La 3ᵉ passe pushout couvre le cas où le snap walkable rentre dans un obstacle. Obstacles considérés "hard constraint" (priorité absolue), walkable "soft constraint" (snap si possible).

**Fichiers créés** :
- `Assets/Scripts/Components/WalkableComponents.cs` : `WalkableArea` (IComponentData), `WalkableSpatialIndex` (singleton).
- `Assets/Scripts/Authoring/WalkableAreaAuthoring.cs` : MonoBehaviour + Baker + gizmos verts (slab plat pour distinguer visuellement des obstacles).
- `Assets/Scripts/Systems/WalkableSpatialIndexSystem.cs` : one-shot, mirror du système obstacle.

**Fichiers modifiés** :
- `Assets/Scripts/Components/ObstacleComponents.cs` : `ObstacleMath` refactoré pour exposer `ClosestPointOnShape` / `WorldAABBOfShape` (overloads primitives partagés).
- `Assets/Scripts/Components/AgentComponents.cs` : champ `WalkableCellSize` ajouté à `SpawnerConfig`.
- `Assets/Scripts/Authoring/CrowdSpawnerAuthoring.cs` : section *Walkable Areas (Phase 2)* dans l'Inspector + écriture dans le Baker.
- `Assets/Scripts/Systems/AgentMovementSystem.cs` : `RequireForUpdate<WalkableSpatialIndex>`, méthode `ConstrainToWalkable` qui cherche dans 3×3 cellules locales (avec fallback brute force), snap au bord le plus proche avec nudge inward 0.05m, kill de la composante outward de la vélocité.

**Procédure de validation à exécuter par l'utilisateur** :

1. Ouvrir `CrowdSubScene`. Ajouter quelques GameObjects vides avec `WalkableAreaAuthoring` :
   - Une zone large couvrant un trottoir principal (HalfExtents ~(10, 0.1, 2)).
   - Une zone perpendiculaire au croisement (chevauche la première de ~0.5m).
2. Play. Les agents doivent :
   - **Rester sur les trottoirs** même quand la séparation ou la répulsion d'obstacle les pousse vers le bord.
   - **Ne pas traverser la route** (zone non-walkable entre deux trottoirs).
3. Test du croisement : un agent qui suit un path traversant deux zones walkable qui se chevauchent doit passer sans à-coups.
4. Test sans walkable areas définies : commenter / supprimer les `WalkableAreaAuthoring`. Le log doit indiquer "walkable constraint disabled" et le comportement doit redevenir celui de Phase 1 (agents libres de bouger n'importe où, sauf dans les obstacles).
5. Vérifier le FPS : la passe walkable est ~75k closest-point checks/frame pour 5000 agents. Devrait coûter < 5% FPS.
6. Si OK : passer le statut à `[x] FAIT — 2026-05-20 — commit <hash>` dans la table en haut **et** dans le titre de cette phase, copier ce bloc en section "Livré".

---

## Phase 3 — Local avoidance (ORCA-lite / RVO simplifié) + behavior dynamics

**Statut : `[x] FAIT — 2026-05-20`**

### But fonctionnel

La séparation actuelle est **purement réactive** : un agent ne dévie qu'une fois trop proche. Conséquence visible : deux flux opposés qui se rencontrent forment un blocage, les agents se bloquent ou se traversent.

On veut de **l'anticipation** : si je vois qu'un agent va me croiser, je dévie *avant* le contact. C'est la signature visuelle qui transforme une foule "particules" en foule "humains".

### Approche

ORCA complet est trop coûteux pour des dizaines de milliers d'agents. On implémente une version **ORCA-lite** :

Pour chaque paire d'agents dans la même cellule du spatial hash + voisines :
1. Calculer le temps avant collision (TTC) en supposant vitesses linéaires constantes.
2. Si TTC < `LookAheadTime` (1-2s) **et** distance courante > `SeparationRadius` (sinon c'est de la séparation pure qui prend le relais) :
   - Calculer la direction de déviation latérale (perpendiculaire à la vitesse relative, du côté qui rapproche le moins du voisin).
   - Force d'évitement ∝ (1 - TTC / LookAheadTime).

### Modifications

Étendre `SteeringJob` dans `AgentSteeringSystem.cs` :
- Le `BuildSpatialHashJob` stocke déjà `Position` et `IsHurried`. **Ajouter la vélocité** dans `AgentSpatialData` (champ `Velocity: float3`).
- Dans la boucle de voisins du steering, calculer TTC :
  ```
  relPos = neighbor.Position - pos
  relVel = movement.Velocity - neighbor.Velocity
  // si relPos . relVel > 0, on s'éloigne : pas de risque
  ttc = - dot(relPos, relVel) / dot(relVel, relVel)
  ```
- Si `0 < ttc < LookAheadTime`, calculer le point de collision projeté et la déviation latérale.

### Paramètres à ajouter à `SpawnerConfig`

```csharp
public float LookAheadTime;       // 1.5f par défaut
public float AvoidanceWeight;     // 2.0f par défaut
```

### Critère de validation

- [ ] Deux groupes de 200 agents qui marchent l'un vers l'autre se croisent en formant deux files distinctes, sans blocage.
- [ ] Pas de saccades visibles dans le mouvement (la composante "anticipation" doit rester lisse).
- [ ] Coût additionnel acceptable (perte < 15% FPS pour 10k agents).

### Notes pour reprise

- Lire l'article "Reciprocal Velocity Obstacles" (van den Berg et al.) si on veut comprendre la théorie. On implémente une version drastiquement simplifiée.
- Ne **pas** désactiver la séparation actuelle : elle reste comme filet de sécurité courte portée. ORCA-lite agit à moyenne portée.

### Livré — 2026-05-20

**Phase 3 a englobé deux features connexes** : l'ORCA-lite (anticipation des trajectoires) et le switch dynamique de behavior (en demande utilisateur, pour éviter que les agents Stationary ne soient figés). Les deux sont intimement liés au système de POIs de Phase 4.

#### ORCA-lite

- **`AgentSpatialData`** étendu avec un champ `Velocity` (nécessaire pour calculer le TTC entre deux agents). `BuildSpatialHashJob` écrit la vélocité courante en plus de la position.
- **Calcul TTC inline** dans la boucle des voisins du `SteeringJob` : sur chaque voisin trouvé, on calcule `t = -dot(diff, rv) / |rv|²` (temps avant approche la plus proche). Si `t ∈ [0, LookAheadTime]` et la distance projetée à `t` est < `AvoidanceCollisionRadius`, on applique une déviation latérale dans la direction perpendiculaire qui éloigne au plus du voisin.
- **Tie-break head-on** : quand deux agents foncent l'un sur l'autre exactement frontalement, `missVec ≈ 0`. On choisit une perpendiculaire à `rv`, et la parité de `entity.Index` détermine le côté → les deux agents choisissent des côtés opposés et ne se mirror-lockent pas.
- **Fenêtre d'action** : séparation reste sur `[0, SeparationRadius]`, ORCA-lite sur `[SeparationRadius, 3 × SeparationRadius]`. Les deux forces s'additionnent dans la même passe sur les voisins (pas de double itération du spatial hash).
- **Params ajoutés** à `SpawnerConfig` : `LookAheadTime` (1.5s défaut), `AvoidanceWeight` (2.0 défaut), `AvoidanceCollisionRadius` (0.7m défaut).

#### Behavior dynamics (driven by POIs)

- **`AgentTypeData`** étendu : `Behavior` (live, ce que l'agent fait maintenant) + `BaseBehavior` (personnalité, Walker ou Hurried) + `BaseSpeed` (vitesse de croisière à restaurer après interaction).
- **`CrowdSpawnerSystem`** : Stationary n'est plus assigné au spawn. Tous les agents reçoivent `BaseBehavior = Walker` ou `HurriedPedestrian` selon `PercentHurried`. `PercentWalker` devient inutilisé (gardé pour compat scènes existantes).
- **`AgentGoalSystem`** orchestre les transitions :
  - **Idle → Traveling** : restaure `Behavior = BaseBehavior`, `Speed = BaseSpeed`. Petite chance (`PersonalitySwapChance = 0.1`) de flipper Walker ↔ Hurried sur le voyage (avec resampling du `BaseSpeed` dans la plage correspondante).
  - **Traveling → Interacting** : sur arrivée au POI, force `Behavior = Stationary`, `Speed = 0`. La steering smoothing décélère naturellement la vélocité, l'animation flip en Idle.
  - **Interacting → Idle** : timer expiré → restaure `Behavior = BaseBehavior`, `Speed = BaseSpeed`. L'agent peut repartir.
- **Stuck recovery étendu** dans `AgentSteeringSystem` : agents en `Traveling` bloqués > 5s abandonnent leur POI courant (`goal.State = Idle`) pour qu'`AgentGoalSystem` re-rolle au tick suivant. Précédemment seuls les path-followers avaient un skip-waypoint à 2s ; les goal-driven étaient potentiellement bloqués indéfiniment derrière un obstacle.

#### Corrections trouvées lors de l'analyse

- **Early-exit foireux dans `AgentGoalSystem.OnUpdate`** : si le buffer POI devenait vide à runtime, les agents en Interacting ne décrémentaient plus leur timer (bloqués pour toujours). Corrigé en faisant tourner le job systématiquement avec un `NativeArray` vide ; les helpers tolèrent déjà 0 POIs.
- **Stuck timer pas reset quand `Speed = 0`** : avec la nouvelle valeur Speed=0 en Interacting, `stallThresholdSq = 0` et le test `length(targetVelocity) < 0` était impossible → pas de problème, mais j'ai explicité une garde `movement.Speed > 0.01f` pour la clarté.

#### Fichiers modifiés

- `Assets/Scripts/Components/AgentComponents.cs` : `AgentTypeData` (+ BaseBehavior, BaseSpeed), `SpawnerConfig` (+ LookAheadTime, AvoidanceWeight, AvoidanceCollisionRadius).
- `Assets/Scripts/Authoring/AgentAuthoring.cs` : init BaseBehavior + BaseSpeed.
- `Assets/Scripts/Authoring/CrowdSpawnerAuthoring.cs` : section "Local Avoidance / ORCA-lite (Phase 3)" exposée dans l'Inspector + écriture au baking.
- `Assets/Scripts/Systems/CrowdSpawnerSystem.cs` : suppression Stationary du spawn, BaseBehavior/BaseSpeed initialisés.
- `Assets/Scripts/Systems/AgentSteeringSystem.cs` : `AgentSpatialData` (+ Velocity), BuildSpatialHashJob (+ in AgentMovement), SteeringJob (+ params ORCA-lite + boucle ORCA dans la passe voisins + combinaison dans le steer + stuck-abandon pour goal-driven).
- `Assets/Scripts/Systems/AgentGoalSystem.cs` : signatures `ref AgentTypeData` + `ref AgentMovement`, transitions de behavior, swap personnalité, suppression early-exit fragile.

#### Procédure de validation à exécuter par l'utilisateur

1. Pose des POIs + obstacles + walkable areas comme dans les phases précédentes. Mets `Capacity` un peu serré sur les POIs (2-3) pour voir le mécanisme de claim/reroll.
2. Play. Observer :
   - Des flux d'agents qui se croisent dévient **avant** le contact (effet ORCA visible : files qui se forment naturellement plutôt que blocage).
   - Les agents arrivés sur un POI **s'arrêtent** (animation Idle) au lieu de marcher sur place.
   - Au bout du dwell time, ils repartent vers un autre POI à pleine vitesse.
   - Une minorité change de "personnalité" entre Walker et Hurried entre deux voyages (visible si on observe un agent quelques minutes).
3. Test stuck-abandon : place un obstacle qui isole complètement un POI. Les agents qui le visent doivent abandonner après 5s et choisir un autre.
4. Vérifier que la séparation courte portée reste active (deux agents collés se repoussent toujours).
5. FPS attendu : surcoût ~5-10% par rapport à Phase 2 (boucle voisins enrichie de quelques opérations).

---

## Phase 4 — Points d'intérêt & comportements à but

**Statut : `[x] FAIT — 2026-05-20`**

### But fonctionnel

Sortir du "loop infini sur waypoints". Un agent doit avoir une **destination** (un POI : banc, vitrine, fontaine, arrêt de bus) et une **raison** d'y aller. Quand il arrive, il **interagit** (s'arrête, joue Idle, occupe une place) pendant un temps, puis se choisit un autre POI.

### Composants & données

**Nouveau fichier `Assets/Scripts/Components/POIComponents.cs`** :

```csharp
public enum POIType : byte
{
    Bench = 0,
    ShopWindow = 1,
    Fountain = 2,
    BusStop = 3,
    StreetFood = 4,
    // étendable
}

public struct PointOfInterest : IComponentData
{
    public POIType Type;
    public float3  Position;
    public int     Capacity;          // combien d'agents peuvent l'occuper simultanément
    public int     CurrentOccupancy;  // managé par AgentGoalSystem
    public float   InteractionRadius; // distance à laquelle l'agent considère "arrivé"
    public float2  DwellTimeRange;    // x = min, y = max secondes
}

public enum AgentGoalState : byte
{
    Idle = 0,        // pas de goal, va en choisir un
    Traveling = 1,   // en route vers TargetPOI
    Interacting = 2, // arrivé, occupe le POI, attend que Timer expire
}

public struct AgentGoal : IComponentData
{
    public Entity         TargetPOI;
    public AgentGoalState State;
    public float          Timer;       // décrémenté en Interacting
}

// Buffer global sur l'entité spawner singleton, pour que les agents trouvent rapidement un POI
public struct POIRef : IBufferElementData
{
    public Entity POIEntity;
}
```

### Authoring

**Nouveau `Assets/Scripts/Authoring/POIAuthoring.cs`** :
- Pose sur chaque banc, vitrine, etc.
- Champs : `Type`, `Capacity`, `InteractionRadius`, `DwellTimeMin/Max`.
- Gizmo : sphère colorée selon le `POIType`, taille = `InteractionRadius`.
- Baker crée une entité avec `PointOfInterest`.

**Modifier `CrowdSpawnerAuthoring`** : ajouter une `List<POIAuthoring> POIs` et le Baker la transforme en buffer `POIRef` sur le singleton spawner.

### Système

**Nouveau `Assets/Scripts/Systems/AgentGoalSystem.cs`** :

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AgentSteeringSystem))]
public partial struct AgentGoalSystem : ISystem
{
    // Pour chaque agent avec AgentGoal :
    //   - Si Idle : choisir un POI compatible (POIType assorti au AgentBehavior) ayant CurrentOccupancy < Capacity
    //     Mettre State = Traveling, écrire un waypoint unique dans son buffer Waypoint (PathFollower pointe sur sa propre entité ou sur le POI).
    //   - Si Traveling : tester distance au POI. Si < InteractionRadius :
    //     State = Interacting, Timer = random(DwellTimeRange), incrémenter CurrentOccupancy du POI.
    //   - Si Interacting : décrémenter Timer. Quand <= 0, décrémenter CurrentOccupancy, repasser à Idle.
}
```

### Modifications corollaires

1. **`AgentSteeringSystem`** : quand l'agent est en `Interacting`, **désactiver la composante path-following** (force `desired = 0`) → l'agent reste en place, seule la séparation joue.

2. **`AgentAnimationSystem`** : quand `State == Interacting`, forcer le clip à `Idle` (la vélocité tombera de toute façon, mais ça évite un frame de transition visible).

3. **`PathFollower`** : devient **secondaire**. Le goal system pilote la destination. Deux options :
   - **Option A (recommandée pour Phase 4)** : le buffer `Waypoint` de l'agent devient dynamique, et le goal system y écrit un seul waypoint = position du POI. L'agent l'atteint en ligne droite (avec évitement d'obstacles de Phase 1).
   - **Option B (attendre Phase 5)** : le goal system fait une `PathRequest`, le pathfinder remplit le buffer avec un vrai chemin A*.

   Pour Phase 4 isolée, prendre Option A.

4. **Compatibilité avec les paths existants** : on garde `AgentBehavior.Walker` qui suit toujours les paths fermés comme avant (pour la flânerie). Seuls `HurriedPedestrian` (qui ont un but : aller au boulot, à un POI "destination") sont affectés. À discuter au moment de l'implémentation.

### Critère de validation

- [ ] Tu vois des agents s'attrouper devant une vitrine (jusqu'à `Capacity`).
- [ ] Un agent qui arrive sur un banc plein dévie et choisit un autre POI.
- [ ] Aucun "blocage" : un agent ne reste pas Traveling indéfiniment vers un POI inatteignable.

### Notes pour reprise

- L'incrémentation atomique de `CurrentOccupancy` doit se faire **single-thread** ou via `Interlocked` (entity command buffer ou un job non-parallèle). Plus simple : faire le `AgentGoalSystem` en `IJobEntity` non-parallèle (pas `.ScheduleParallel()`).
- Penser à un fallback "POI cible détruit" : si `TargetPOI == Entity.Null` ou plus de buffer, repasser à Idle.

---

## Phase 5 — Pathfinding A* sur grille

**Statut : `[ ] TODO`**

### But fonctionnel

Permettre à un agent de calculer un chemin entre A et B en évitant les obstacles, au lieu de suivre des waypoints pré-tracés. Indispensable pour des scènes complexes (impasses, détours).

### Approche

**Grille 2D régulière** au-dessus de la scène, cellules ~1m. Chaque cellule est marquée :
- `Walkable = true` si au moins une `WalkableArea` (Phase 2) la recouvre **ET** aucun `StaticObstacle` (Phase 1) ne la recouvre.
- `Walkable = false` sinon.

Bake cette grille **une seule fois au boot** dans un singleton `NavGrid`.

### Composants

```csharp
public struct NavGrid : IComponentData
{
    public float3 Origin;       // coin bas-gauche monde
    public float  CellSize;
    public int    Cols;
    public int    Rows;
}

// Buffer plat de Cols*Rows booléens (utiliser byte ou bitset compact)
public struct NavCell : IBufferElementData
{
    public byte Walkable;
}

public struct PathRequest : IComponentData
{
    public float3 Start;
    public float3 Goal;
    public byte   Status;  // 0 = Pending, 1 = Computing, 2 = Done, 3 = Failed
}

[InternalBufferCapacity(0)]
public struct PathResult : IBufferElementData
{
    public float3 Position;
}
```

### Système

**Nouveau `Assets/Scripts/Systems/PathfindingSystem.cs`** :
- Consomme la queue des entités ayant un `PathRequest` avec `Status == Pending`.
- **Budget par frame** : N requêtes max (config, par exemple 32).
- Pour chaque requête : A* classique sur la grille, écrit le résultat dans le buffer `PathResult` de l'entité, met `Status = Done`.
- Chaque requête tourne dans un `IJob` séparé (pas `IJobEntity`, car chaque agent a sa propre data).

### Intégration avec Phase 4

`AgentGoalSystem` :
- Quand un agent passe à `Traveling`, **émettre un `PathRequest`** au lieu d'écrire un waypoint unique.
- Quand le `PathRequest.Status == Done`, copier le `PathResult` dans le buffer `Waypoint` de l'agent, mettre `PathFollower.CurrentWaypoint = 0`, le steering classique reprend la suite.

### Critère de validation

- [ ] Un agent dont le chemin direct est bloqué par un bâtiment contourne via la rue adjacente.
- [ ] Tu déplaces un obstacle en runtime (test à la main) → les nouveaux agents recalculent et contournent. (La grille doit être recalculable en runtime, prévoir un `RebuildNavGrid` côté éditeur.)
- [ ] Budget de 32 requêtes/frame tient la cadence sans spike pour 5000 agents.

### Notes pour reprise

- A* sur grille à ~1m de cellule sur une carte 200×200 = 40 000 cellules. Acceptable, mais penser à **JPS (Jump Point Search)** ou hiérarchique si la carte devient grande.
- Le résultat brut de A* est un chemin en escalier. **Lisser** avec un line-of-sight check : si je peux aller directement de waypoint N à N+2 sans traverser de bloc, supprimer N+1.
- **Cache de chemins** : deux agents qui vont du même quartier au même POI peuvent partager le chemin. Optimisation Phase 5.5 si nécessaire.

---

## Phase 6 — Interactions sociales (groupes, rencontres)

**Statut : `[ ] TODO`**

### But fonctionnel

Les agents existent **les uns par rapport aux autres**, pas seulement comme obstacles à éviter. Deux mécaniques :
1. **Groupes** : amis/famille qui marchent ensemble (cohésion douce).
2. **Rencontres** : 2 agents qui se croisent → faible chance de s'arrêter brièvement.

### Composants

```csharp
[InternalBufferCapacity(3)]
public struct SocialLink : IBufferElementData
{
    public Entity Friend;
}

public struct EncounterState : IComponentData
{
    public Entity PartnerEntity;  // Null si pas en rencontre
    public float  Timer;          // décompte avant de repartir
}
```

### Spawn

Modifier `CrowdSpawnerSystem` :
- Après instanciation, regrouper les agents par paires/triplets aléatoirement (~30% en groupe, 70% solo).
- Pour chaque agent du groupe, écrire les autres dans son buffer `SocialLink`.
- Synchroniser leur POI cible initial : s'ils sont du même groupe, ils partagent la première destination.

### Systèmes

**Nouveau `Assets/Scripts/Systems/SocialSystem.cs`** :
- **GroupCoherenceJob** : pour chaque agent, calculer la position moyenne de ses amis. Ajouter une force de cohésion (attractive, faible) vers ce point. À combiner avec la séparation existante (pas de remplacement).
- **EncounterJob** : pour chaque paire d'agents proches (< 1.5m) avec faible vélocité (< 0.3 m/s), tirer 1% de chance par seconde de déclencher une rencontre. Si déclenchée, écrire `EncounterState.PartnerEntity` mutuel et `Timer` random(3-8s).
- **EncounterUpdateJob** : si `PartnerEntity != Null`, forcer `desired = 0` (l'agent reste en place), face son partner via rotation. Décompter Timer. Quand 0, libérer mutuellement.

### Modifications corollaires

- `AgentAnimationSystem` : quand `EncounterState.PartnerEntity != Null`, forcer clip `Idle`. Plus tard si on bake un clip "Talk", l'utiliser ici.
- `AgentSteeringSystem` : à l'extérieur du job, **pousser la force de cohésion** comme nouvelle composante, ou la combiner directement dans le steering existant.

### Critère de validation

- [ ] Tu repères à l'œil des paires/triplets d'agents qui marchent ensemble sur de longues distances.
- [ ] Tu vois des conversations ponctuelles (2 agents arrêtés face à face quelques secondes) sans les avoir scriptés.
- [ ] Pas de "cluster lock" : un groupe peut se séparer si un POI cible diverge.

### Notes pour reprise

- Plafonner `SocialLink` à 3 (cohésion serrée). Au-delà, c'est un attroupement, géré par les POIs.
- Si on veut des familles cohérentes visuellement, prévoir un `GroupAppearance` (même tenue / variante) plus tard — pas dans cette phase.

---

(retour à Phase 4 — section ajoutée a posteriori car implémentée juste après Phase 2)

### État de l'implémentation Phase 4 (à valider en Unity)

**Code livré le 2026-05-20** — choix : sauter Phase 3 (ORCA) pour cette itération, faire Phase 4 directement après Phase 2 car les POIs sont l'option naturelle pour remplacer les paths obligatoires.

**Écarts par rapport au plan initial** :

- **AgentGoal ajouté à TOUS les agents** (sauf décision dynamique du goal system), pas seulement aux agents avec POIs. La transition entre `Idle` (pas de goal actif) et `Traveling`/`Interacting` se fait runtime. Quand le buffer `POIRef` est vide, le goal system retourne early et tous les agents restent `Idle` → fallback automatique sur `PathFollower`. Permet de garder la compat Phase 2 sans avoir à enlever AgentGoal côté authoring.
- **Pas de réservation de POI au démarrage du voyage**. Plusieurs agents peuvent cibler le même POI en parallèle ; le premier qui arrive claim un slot, les autres trouvent le POI plein et rerollent. Plus simple, légèrement gaspillé (latency d'1 frame) mais robuste face à la destruction de POI runtime.
- **`AgentGoalSystem` tourne en single-thread (`Schedule`, pas `ScheduleParallel`)**. C'est ce que le roadmap recommandait pour la sécurité des incréments de `CurrentOccupancy`. Le coût est négligeable car la logique de but est minimale (quelques comparaisons et une écriture par agent).
- **`AgentAnimationSystem` modifié** : quand `goal.State == Interacting`, force le clip `Idle` même si la vélocité résiduelle dépasserait encore le seuil de marche. Évite les jambes qui marchent sur place pendant ~1s après arrivée à un POI.
- **Override du steering** : `goal.State == Traveling` → desired = direction vers `TargetPosition` (court-circuite le PathFollower). `goal.State == Interacting` → desired = 0 (immobile). `goal.State == Idle` → fallback PathFollower / Stationary comme avant.
- **Stuck-skip Phase 1 désactivé quand le goal pilote**. Sauter au prochain waypoint d'un path n'a aucun sens quand la destination est un POI ; on garde le cap.

**Fichiers créés** :
- `Assets/Scripts/Components/POIComponents.cs` : `POIType`, `PointOfInterest`, `POIRef` (buffer), `AgentGoalState`, `AgentGoal`.
- `Assets/Scripts/Authoring/POIAuthoring.cs` : MonoBehaviour avec gizmo color-coded par `POIType` + cercle au sol pour visualiser `InteractionRadius`.
- `Assets/Scripts/Systems/AgentGoalSystem.cs` : machine à états Idle ↔ Traveling ↔ Interacting, single-thread, Burst.

**Fichiers modifiés** :
- `Assets/Scripts/Authoring/AgentAuthoring.cs` : ajoute `AgentGoal` (Idle par défaut) à chaque agent au baking.
- `Assets/Scripts/Authoring/CrowdSpawnerAuthoring.cs` : champ `List<POIAuthoring> POIs` exposé, baker écrit dans un buffer `POIRef` sur l'entité singleton.
- `Assets/Scripts/Systems/AgentSteeringSystem.cs` : `ref AgentGoal goal` ajouté au job ; override du `desired` selon `goal.State` ; `_agentQuery` étendue à AgentGoal ; stuck-detection ignore les agents dont le goal pilote.
- `Assets/Scripts/Systems/AgentAnimationSystem.cs` : `in AgentGoal goal` ajouté ; force le clip Idle quand `state == Interacting`.

**Procédure de validation à exécuter par l'utilisateur** :

1. Pose 3-5 GameObjects vides dans la scène, ajoute-leur `POIAuthoring`. Varie les `POIType` (Bench, ShopWindow, Fountain) pour voir les gizmos colorés (marron, cyan, bleu).
2. Drag-and-drop ces objets dans le `List<POIAuthoring> POIs` du `CrowdSpawnerAuthoring`. **Important** : un POI qui n'est pas dans cette liste ne sera jamais ciblé.
3. Play. Comportement attendu :
   - Chaque agent non-stationnaire se choisit un POI au démarrage et s'y dirige.
   - À l'arrivée (distance < `InteractionRadius`), l'agent s'arrête et joue Idle pendant 5-15s (selon `DwellTimeRange`).
   - Quand le timer expire, l'agent repart vers un autre POI.
   - Si un POI est plein (capacity atteinte), les agents qui arrivent rerollent vers un autre.
4. Test fallback : retire tous les POIs de la liste (mais garde les paths) → les agents retombent sur le comportement Phase 2 (suivi de paths).
5. Vérifier que les agents qui interagissent jouent bien l'animation Idle (pas Walk).
6. Si OK : passer le statut à `[x] FAIT — 2026-05-20 — commit <hash>` dans la table en haut **et** dans le titre Phase 4, copier ce bloc en section "Livré".

---

## Phase 7 — Cycle jour/nuit & schedules

**Statut : `[ ] TODO`**

### But fonctionnel

La foule respire à l'échelle de la journée : pic matinal d'`Hurried` (rush vers le travail), midi animé (POIs "restaurant"), soir relax, nuit calme.

### Composants

```csharp
public struct WorldClock : IComponentData
{
    public float TimeOfDay;       // 0..24 heures
    public float TimeScale;       // 1 = temps réel, 60 = 1 min réelle = 1h simulée
}
```

### Authoring & paramétrage

**`WorldClockAuthoring`** : MonoBehaviour qui pose `WorldClock` sur une entité singleton, expose `StartHour` et `TimeScale`.

**Étendre `POIAuthoring`** : ajouter un `ActiveTimeRange (float2)` : un restaurant n'est "actif" que 11h-14h et 19h-22h. Un POI inactif a `Capacity = 0` runtime.

**Étendre `SpawnerConfig`** : ajouter une **table de distribution par heure** :
```csharp
// 24 entrées, une par heure. Chaque entrée = ratio (Hurried, Walker, Stationary).
public BlobAssetReference<HourlyDistribution> HourlyDistribution;
```

### Système

**Nouveau `Assets/Scripts/Systems/WorldClockSystem.cs`** :
- Avance `TimeOfDay` à chaque frame de `Time.DeltaTime * TimeScale`.
- À chaque heure pleine simulée :
  - Recalcule `PointOfInterest.Capacity` selon `ActiveTimeRange`.
  - Émet un événement (composant cleanup `HourChangedEvent`) que le `AgentGoalSystem` consomme pour rééquilibrer les goals.

### Critère de validation

- [ ] Time-lapse 1 journée simulée (TimeScale = 600) : la densité varie, les flux changent visiblement.
- [ ] Un POI "BusStop" se vide en pleine nuit, se remplit aux heures de pointe.
- [ ] Pas de glitch à minuit (passage 23.99 → 0.0).

### Notes pour reprise

- Pas d'ambiance visuelle (skybox, lumière) dans cette phase — uniquement la **simulation** suit le temps. L'ambiance graphique = projet à part.
- Si on veut spawn/despawn massif aux heures de pointe, **enchaîner avec Phase 8**.

---

## Phase 8 — Spawn/despawn aux bords (cycle de vie)

**Statut : `[ ] TODO`**

### But fonctionnel

Au lieu de spawn tout au démarrage, la population est **renouvelée en continu** : des agents entrent par des portails aux bords de la scène et en sortent par d'autres portails. Permet d'avoir la même densité visuelle avec des "visages" différents au fil du temps, et de moduler la population selon l'heure (Phase 7).

### Composants

```csharp
public enum PortalKind : byte { Spawn = 0, Despawn = 1, Both = 2 }

public struct Portal : IComponentData
{
    public PortalKind Kind;
    public float3     Position;
    public float      Radius;
    public float      SpawnRatePerSec;  // ignoré si Kind == Despawn
}
```

### Authoring

**`PortalAuthoring`** : pose aux entrées de map. Gizmo cyan, taille = `Radius`.

### Système

**Nouveau `Assets/Scripts/Systems/PortalSpawnSystem.cs`** :
- À chaque frame, pour chaque portail Spawn : accumule `SpawnRatePerSec * deltaTime` dans un compteur interne. Quand >= 1, spawn un agent (logique copiée/réutilisée de `CrowdSpawnerSystem`).
- Pour chaque portail Despawn : tout agent qui passe dans son `Radius` avec un `AgentGoal.State == Traveling` vers un POI proche de ce portail est détruit (ECB).

### Modifications corollaires

- **`CrowdSpawnerSystem`** devient optionnel : si la scène utilise des portails, on peut décider de **ne pas spawner de population initiale** (ou en spawner peu, le reste arrive progressivement). Ajouter un flag `UsePortalSystem` dans `CrowdSpawnerAuthoring`.
- **Synergie Phase 7** : `Portal.SpawnRatePerSec` peut être pondéré par l'heure (table 24 entrées par portail).

### Critère de validation

- [ ] Population stable autour d'un nombre cible, mais entités différentes à 1 min d'intervalle.
- [ ] Aux heures de pointe (Phase 7), la densité augmente visiblement.
- [ ] Pas de fuite mémoire : les entités détruites le sont proprement (pas de buffer orphelin).

### Notes pour reprise

- Penser au pooling : recycler les entités plutôt que destroy/instantiate, si le coût se voit dans le profiler.
- Les groupes (Phase 6) doivent spawn ensemble depuis le même portail.

---

## Phase 9 — Perception & réactions à événements

**Statut : `[ ] TODO`**

### But fonctionnel

La foule réagit à des événements ponctuels : un bruit fort, un point d'intérêt soudain (clic souris), une "alerte". Les agents proches modifient leur goal (regarder, fuir, s'attrouper).

### Composants

```csharp
public enum StimulusKind : byte
{
    Attention = 0,  // les agents se tournent et regardent
    Flee = 1,       // les agents fuient radialement
    Attract = 2,    // les agents convergent (foule de curieux)
}

public struct StimulusEvent : IComponentData
{
    public StimulusKind Kind;
    public float3       Position;
    public float        Radius;
    public float        Intensity;   // 0..1
    public float        ExpiresAt;   // ElapsedTime + duration
}
```

### Émission d'événements

- Depuis un script éditeur : clic gauche → spawn entité avec `StimulusEvent { Kind = Attention, ... }`.
- Depuis un script gameplay : explosion, sirène, etc.

### Systèmes

**Nouveau `Assets/Scripts/Systems/PerceptionSystem.cs`** :
- Pour chaque `StimulusEvent` actif, parcourir les agents dans le rayon.
- Selon `Kind` :
  - `Attention` : modifie `LocalTransform.Rotation` pour orienter vers le stimulus pendant 1-2s (override léger, ne casse pas le steering — juste la rotation visuelle).
  - `Flee` : suspend `AgentGoal`, écrit un waypoint temporaire à l'opposé du stimulus, augmente `Speed * 2` temporairement.
  - `Attract` : suspend `AgentGoal`, ajoute la position du stimulus comme POI temporaire (avec Capacity élevée).

**Nouveau `Assets/Scripts/Systems/StimulusCleanupSystem.cs`** :
- À chaque frame, détruit les `StimulusEvent` dont `ExpiresAt < ElapsedTime`.

### Critère de validation

- [ ] Clic gauche sur le sol → tous les agents dans 5m tournent la tête vers le point.
- [ ] Déclencher un `Flee` au centre d'un groupe → vague de fuite radiale visible, agents reprennent leur goal après 5s.
- [ ] Un `Attract` crée un attroupement temporaire, qui se disperse après expiration.

### Notes pour reprise

- L'override de rotation pour `Attention` est délicat car le `AgentMovementSystem` recalcule la rotation depuis la vélocité. Soit on ajoute un flag "RotationOverridden", soit on accepte que l'attention ne dure que pendant l'arrêt.
- Cleanup robuste : si un agent est en `Flee` et que le stimulus est détruit avant la fin, l'agent doit reprendre son ancien `AgentGoal` (sauvegarder l'état précédent).

---

## Phase 10 — Debug tooling

**Statut : `[ ] TODO`**

### But fonctionnel

Pouvoir diagnostiquer la simu à grande échelle sans deviner : où s'accumule la densité, quels POIs sont saturés, pourquoi un agent est bloqué.

### Outils à livrer

1. **Heatmap de densité** : compute shader qui agrège le nombre d'agents par cellule de la grille spatiale (`AgentSteeringSystem._spatialHash`), affiche en overlay couleur dans la scène. Toggle dans le `CrowdHUD`.

2. **Vector field overlay** : pour chaque cellule, dessine une flèche = vélocité moyenne des agents qui s'y trouvent. Permet de voir les flux dominants.

3. **Inspecteur d'agent** : un script Editor qui, quand on clique sur un agent dans Scene View, ouvre une fenêtre affichant :
   - Son `AgentGoal` (état, POI cible, timer)
   - Son chemin (waypoints restants, dessinés en lignes vertes)
   - Ses voisins immédiats (cercles bleus)
   - Sa vélocité (vecteur rouge)

4. **Stats étendues dans `CrowdHUD`** :
   - Nb d'agents par état (`Traveling`, `Interacting`, `Idle`)
   - Nb de POIs saturés
   - Densité max sur la grille

### Critère de validation

- [ ] Tu peux justifier en 10s pourquoi un agent est bloqué (regarde sa heatmap → cluster → POI saturé).
- [ ] La heatmap tient 60 FPS pour 30k agents.

### Notes pour reprise

- Le compute shader pour la heatmap peut consommer directement `AgentSpatialData` exposée par le steering system — pas besoin de double buffer.
- L'inspecteur d'agent passe par `EntitySelectionProxy` (déjà utilisé par Unity dans l'Entities Hierarchy).

---

# Branche véhicules — Phases 11 à 15

Cette branche est **indépendante** des Phases 5-10 et peut être tirée en parallèle. Elle introduit les voitures comme deuxième archétype d'entité, l'infrastructure routière dédiée (routes, passages piétons), et la coordination bidirectionnelle entre piétons et véhicules. L'objectif final : une ville où voitures et piétons coexistent sans script — la simulation produit naturellement des arrêts aux passages piétons, des files de voitures derrière une lente, des piétons qui attendent au feu.

**Principes architecturaux (à respecter dans toutes les phases véhicules)** :

- **Archétypes séparés** : les voitures n'ont AUCUN composant en commun avec `AgentTag` côté logique. Une voiture n'est ni un agent qui marche vite, ni un obstacle dynamique — c'est un troisième type d'entité. Cela permet à Burst de bien spécialiser les chunks et évite que des systèmes piétons itèrent inutilement sur des voitures.
- **Spatial hash séparé** : les voitures sont peu nombreuses (~50-200) mais rapides et grandes. Cell size dédiée (typiquement 6-10m). Le hash piéton (cell ~1.5m) reste inchangé. Pas de cross-iteration ; la symbiose (Phase 14) interroge le hash voiture depuis les piétons et vice versa, mais sans les mélanger.
- **Données immuables en `BlobAssetReference`** : le **lane graph** (réseau de voies + connexions aux intersections) est statique côté scène. Le baker produit un `BlobAssetReference<LaneGraph>` que tous les jobs lisent en parallèle sans coût. Pattern DOTS standard pour graphes/maillages statiques.
- **Cinématique, pas physique** : les voitures intègrent vélocité avec accélération/freinage réalistes (`CurrentSpeed += accel * dt`, asymétrique entre accel et brake), mais n'utilisent **pas** Unity Physics. La détection de collision passe par le spatial hash et le lane graph. Évite la complexité PhysX et garde la simulation déterministe + Burst-friendly.
- **Pas de structural changes runtime** : un changement de voie d'une voiture ne crée/supprime PAS de composant. On modifie les valeurs (`LaneFollower.CurrentLane`). Les structural changes sont coûteux en ECS ; les valeurs sont gratuites.
- **Tous les jobs `[BurstCompile]` + `ScheduleParallel` quand possible** : seules les rares mutations partagées (ex: compteurs d'occupation crosswalk) sont single-thread.

---

## Phase 11 — Infrastructure routière (roads & crosswalks)

**Statut : `[ ] TODO`**

### But fonctionnel

Étendre la grammaire des zones du jeu : aujourd'hui on a `WalkableArea` (piétons OK) et `StaticObstacle` (interdit à tous). On ajoute :

- **`RoadZone`** : où les voitures roulent. Les piétons DOIVENT l'éviter (sauf via un crosswalk).
- **`CrosswalkZone`** : passage piéton. Ouvre une "fenêtre" dans la route où les piétons peuvent traverser sous conditions (sécurité Phase 14, feux Phase 14 bis).

C'est une phase **infrastructure-only** : zéro nouveau comportement, zéro nouvelle entité dynamique. On pose les zones, les composants ECS, les gizmos, les index spatiaux. Les piétons commencent à respecter les routes (qu'ils traitent comme des zones non-walkable étendues) — la traversée via crosswalk vient en Phase 14 avec la symbiose voiture.

### Données & composants

**Nouveau fichier `Assets/Scripts/Components/RoadComponents.cs`** :

```csharp
public struct RoadZone : IComponentData
{
    public ObstacleShape Shape;       // réutilise l'enum Box/Circle existant
    public float3 Center;
    public float3 HalfExtents;
    public float RotationY;
    public float SpeedLimit;          // m/s, pour info + Phase 13 (cars cap leur TargetSpeed)
    public byte LaneCount;            // nombre de voies (utilisé en Phase 12 pour répartir les lanes)
}

public enum CrosswalkSignal : byte
{
    AlwaysGreen = 0,   // sans feu (zone résidentielle calme)
    Timed = 1,         // cycle automatique Phase 14
    Demand = 2,        // déclenché par présence piéton (futur)
}

public struct CrosswalkZone : IComponentData
{
    public ObstacleShape Shape;
    public float3 Center;
    public float3 HalfExtents;
    public float RotationY;
    public CrosswalkSignal SignalType;
    public float SignalCycleDuration; // secondes, ignoré si AlwaysGreen
    public float SignalPhaseOffset;   // pour désynchroniser plusieurs crosswalks
}

// Singletons spatiaux (mêmes patterns que ObstacleSpatialIndex)
public struct RoadSpatialIndex : IComponentData
{
    public NativeArray<RoadZone> Roads;
    public NativeParallelMultiHashMap<int, int> CellToRoadIndex;
    public float CellSize;
    public byte IsBuilt;
    public byte HasRoads;
}

public struct CrosswalkSpatialIndex : IComponentData
{
    public NativeArray<CrosswalkZone> Crosswalks;
    public NativeParallelMultiHashMap<int, int> CellToCrosswalkIndex;
    public float CellSize;
    public byte IsBuilt;
    public byte HasCrosswalks;
}
```

### Authoring

- **`RoadAuthoring.cs`** — MonoBehaviour ; gizmo gris foncé (slab moyen ~0.1m d'épaisseur, distinct des Walkable slabs verts). Inspector : Shape, HalfExtents, SpeedLimit (km/h, converti en m/s au baker), LaneCount.
- **`CrosswalkAuthoring.cs`** — gizmo blanc rayé (lignes horizontales) ou cyan pâle. Inspector : Shape, HalfExtents, SignalType, SignalCycleDuration, SignalPhaseOffset.

### Systèmes

- **`RoadSpatialIndexSystem.cs`** — miroir d'`ObstacleSpatialIndexSystem`, one-shot dans `InitializationSystemGroup`.
- **`CrosswalkSpatialIndexSystem.cs`** — idem.

### Modifications corollaires

- **`AgentMovementSystem.cs`** :
  - Étendre la contrainte walkable pour traiter les `CrosswalkZone` comme une **extension dynamique** de walkable (un agent à l'intérieur d'un crosswalk est considéré walkable même hors `WalkableArea`).
  - Ajouter un **pushout des `RoadZone`** (les piétons qui finissent sur la route via overshoot sont snappés au bord, comme pour les obstacles). Le test "suis-je sur la route ?" passe AVANT le test walkable ; si oui ET pas sur un crosswalk → push back vers le walkable le plus proche.
- **`AgentSteeringSystem.cs`** :
  - Optionnel V11.5 : ajouter un soft-repel léger pour pénaliser le `desired` qui pointe vers une `RoadZone`. Pour Phase 11 v1, on peut s'en passer (le pushout suffit), à voir au test.

### Critère de validation

- [ ] Pose une `RoadZone` qui sépare deux trottoirs (`WalkableArea`). Les piétons restent sur leur trottoir, ne traversent pas. Visuel : zone grise distincte.
- [ ] Ajoute un `CrosswalkZone` qui chevauche la route et les deux trottoirs. Les piétons qui passent à travers peuvent y rester (n'est plus snappé). Visuel : rayé blanc.
- [ ] Aucun comportement véhicule encore — uniquement de la géométrie.
- [ ] Perf : pas de régression notable (les nouveaux index sont sparse).

### Notes pour reprise

- Le pattern `XxxSpatialIndexSystem` commence à se répéter (obstacles, walkable, road, crosswalk). À partir de Phase 11, considérer un helper générique `Build2DSpatialIndex<T>` parameterisable pour DRY. Pas critique mais propre.
- Les `CrosswalkZone` DOIVENT chevaucher à la fois une `RoadZone` et au moins une `WalkableArea` adjacente — sinon Phase 14 ne saura pas connecter "côté piéton" et "côté route". Documenter dans le tooltip authoring.

---

## Phase 12 — Voitures : entités, voies, conduite de base

**Statut : `[ ] TODO`**

### But fonctionnel

Faire rouler des voitures dans la ville. Elles suivent des **voies orientées** (lanes), respectent leur `RoadZone`, accélèrent et freinent de manière réaliste. **PAS** d'IA d'évitement entre voitures (Phase 13) ni de symbiose piéton (Phase 14) à ce stade — uniquement la "physique" + le pathing de base.

### Données & composants

**Nouveau fichier `Assets/Scripts/Components/CarComponents.cs`** :

```csharp
public struct CarTag : IComponentData { }

public struct CarMovement : IComponentData
{
    public float3 Velocity;          // m/s, dérivée de CurrentSpeed * direction
    public float CurrentSpeed;       // m/s
    public float TargetSpeed;        // m/s, fixé par les systèmes amont
    public float Acceleration;       // m/s² quand TargetSpeed > CurrentSpeed
    public float BrakeForce;         // m/s² (positif) quand TargetSpeed < CurrentSpeed
}

public struct CarTypeData : IComponentData
{
    public float MaxSpeed;           // m/s, plafond absolu
    public float Length;             // longueur du véhicule (collision + adaptive cruise)
    public float Width;
}

public struct LaneFollower : IComponentData
{
    public Entity CurrentLane;       // entité Lane que la voiture suit
    public int   NodeIndex;          // segment courant dans le buffer LaneNode
    public float ProgressAlongSegment; // 0..1 entre NodeIndex et NodeIndex+1
    public Entity NextLane;          // déterminé à l'approche de la fin (intersection)
}

public struct LaneTag : IComponentData { }

[InternalBufferCapacity(0)]
public struct LaneNode : IBufferElementData
{
    public float3 Position;
}

[InternalBufferCapacity(0)]
public struct LaneConnection : IBufferElementData
{
    public Entity NextLane;          // voies possibles à la sortie (intersection)
}
```

**Lane Graph (BlobAsset)** — pattern DOTS pour graphe statique :

```csharp
public struct LaneGraph
{
    public BlobArray<BlobLaneNode> Lanes;        // toutes les voies
    public BlobArray<int> ConnectionIndices;     // indices vers BlobLaneNode pour chaque connection
}

public struct BlobLaneNode
{
    public BlobArray<float3> Nodes;
    public int ConnectionStart;
    public int ConnectionCount;
    public float MaxSpeed;
}
```

Construit au baking par `LaneGraphBakingSystem`, stocké en singleton. Lecture lock-free dans tous les jobs.

### Authoring

- **`LaneAuthoring.cs`** — analogue à `PathAuthoring` mais **orienté** (du premier au dernier enfant), pas de boucle. Gizmo : flèches grises le long du chemin, taille du nœud = largeur de la voie. Champ `MaxSpeed` (m/s), liste `List<LaneAuthoring> ConnectionsAtEnd` pour câbler les intersections manuellement (Phase 12 v1) — un baker plus malin pourra inférer les connexions par proximité plus tard.
- **`CarAuthoring.cs`** — prefab voiture. Mesh placeholder = cube allongé 4×1.5×2m pour V12 (mesh complexe plus tard). Champs : MaxSpeed, Acceleration, BrakeForce, Length, Width.
- **`CarSpawnerAuthoring.cs`** — analogue à `CrowdSpawnerAuthoring` mais pour voitures. Référence le prefab car, un `List<LaneAuthoring>` de voies de spawn, un Count cible. Optionnel : `SpawnRatePerSec` pour spawn continu vs one-shot.

### Systèmes

- **`CarSpawnerSystem.cs`** — instancie les voitures au démarrage, les place sur des nodes de leurs lanes assignées avec offset progressif pour éviter de spawn deux voitures empilées.
- **`CarLaneFollowingSystem.cs`** — `[BurstCompile]`, `ScheduleParallel`. Pour chaque voiture : calcule la direction depuis la position vers le prochain node de la lane. À l'approche du dernier node, sélectionne `NextLane` parmi `LaneConnection` (aléatoire avec seed entity ; Phase 13 affinera avec des règles de trafic).
- **`CarMovementSystem.cs`** — `[BurstCompile]`, `ScheduleParallel`. Intègre `CurrentSpeed` vers `TargetSpeed` avec `Acceleration` ou `BrakeForce` selon le signe. Applique `CurrentSpeed * direction` à `LocalTransform.Position`. Rotation Y dérivée de la direction. Plafond `MaxSpeed`.
- **`CarRoadConstraintSystem.cs`** (optionnel) — si une voiture sort de sa `RoadZone` (anomalie), snap vers le centre de la lane. Filet de sécurité.

### Modifications corollaires

- Aucune côté piéton (les voitures sont une archétype séparée). Les piétons les ignorent encore — la coordination vient en Phase 14.
- Le `MainScene` aura besoin de quelques lanes posées manuellement pour la première validation. Documenter ça.

### Critère de validation

- [ ] 3 lanes droites en ligne (lane A → lane B → lane C, chaînées). Spawn 5 voitures sur A. Elles roulent jusqu'à C, restent sur la route, accélèrent jusqu'à MaxSpeed.
- [ ] À une intersection à 2 sorties (lane B → lane C ou lane D), les voitures se répartissent aléatoirement à peu près moitié-moitié.
- [ ] Pas de collision encore — les voitures se traversent si elles arrivent au même point. Normal pour Phase 12.
- [ ] Animation visuelle : rotation correcte dans les virages, accélération réaliste (pas instantanée).
- [ ] FPS : 100 voitures stables à 60 FPS.

### Notes pour reprise

- Le **BlobAsset** pour le lane graph est le détail technique le plus important. Sans ça, les systèmes feraient des `ComponentLookup<LaneFollower>` partout, coûteux. Le blob permet à chaque voiture de hop d'une lane à l'autre sans lookup runtime.
- Pour les virages serrés, considérer un **lissage tangent** : interpoler entre nodes via une spline plutôt que des segments droits. Pas critique V12, à voir si le rendu pique les yeux.
- Voitures et piétons ne partagent pas LocalTransform.y ; les voitures peuvent rester à y=0 comme les piétons pour V12. La 3D verticale (rampes, ponts) est un projet à part.

---

## Phase 13 — IA trafic : voiture ↔ voiture

**Statut : `[ ] TODO`**

### But fonctionnel

Les voitures ne se traversent plus. Deux mécaniques :

1. **Adaptive cruise control** : une voiture qui suit une autre plus lente sur la même lane ralentit pour maintenir une distance de sécurité.
2. **Intersection priority** : à un croisement entre deux lanes, la voiture arrivée la première passe, les autres attendent (FIFO simple ; règles plus fines = futur).

### Approche

#### Spatial hash voiture dédié

```csharp
public struct CarSpatialData
{
    public Entity Entity;
    public float3 Position;
    public float3 ForwardDir;     // direction normalisée
    public float CurrentSpeed;
    public Entity CurrentLane;
}

public struct CarSpatialIndex : IComponentData
{
    public NativeParallelMultiHashMap<int, CarSpatialData> Map;
    public float CellSize;        // ~8m (3-4× la longueur d'une voiture)
}
```

Construit chaque frame par `BuildCarSpatialHashJob` (parallèle, écrit dans un ParallelWriter), analogue à `BuildSpatialHashJob` piéton.

#### Adaptive cruise (suivi de file)

Dans `CarLaneFollowingSystem` (ou nouveau `CarFollowingSystem` post-lane) :
- Pour chaque voiture, scan le cône avant (longueur = 2 × TimeHeadway × CurrentSpeed + minDistance, demi-angle ~10°).
- Trouve la voiture la plus proche dans ce cône **sur la même lane** (le `CurrentLane` du voisin doit matcher, sinon c'est probablement une voiture qui croise).
- Si distance < safetyDistance : `TargetSpeed = neighbor.CurrentSpeed - smallMargin`.
- Si distance >> safetyDistance : `TargetSpeed = laneMaxSpeed`.
- L'accélération/freinage existante de `CarMovementSystem` gère la transition naturellement.

#### Intersection priority

- **Lane graph étendu** : chaque `LaneConnection` qui mène à une intersection référence une "intersection zone" (`Entity` ou index dans un buffer global).
- **Composant `IntersectionState`** : nb de voitures actuellement dans la zone, queue d'attente (par FIFO arrival time).
- À l'approche d'une intersection :
  - Voiture s'enregistre dans la queue (`enqueueTime = ElapsedTime`).
  - Si elle est en tête de queue ET aucune voiture n'est actuellement dans la zone d'une lane en conflit → entre, sa `TargetSpeed` reste `laneMaxSpeed`.
  - Sinon → `TargetSpeed = 0` (freine devant la ligne d'arrêt).
- À la sortie de la zone : se retire de la queue, libère pour la suivante.

V1 simple : tout en single-threaded sur les transitions (modifs partagées sur `IntersectionState`). 50-100 intersections × ~10 voitures = 500-1000 ops/frame, négligeable.

### Modifications corollaires

- `CarSpawnerSystem` : assigne le `Length` du véhicule dans l'init (utilisé par adaptive cruise).
- Aucune côté piéton.

### Critère de validation

- [ ] 10 voitures sur une lane droite. La première freine à un certain point. Les suivantes freinent en cascade, gardent un gap stable.
- [ ] 4 lanes convergeant vers une intersection (croisement en + classique). Les voitures alternent leur passage proprement, pas de pile-up.
- [ ] Stress test : 100 voitures sur un réseau de 5 intersections. Pas d'interpénétration, le réseau ne se grippe pas (pas de deadlock).
- [ ] FPS : 100 voitures à 60 FPS toujours stable malgré le cone-search ajouté.

### Notes pour reprise

- L'**adaptive cruise** est le morceau le plus visuel — bien la régler. TimeHeadway (gap en secondes plutôt qu'en mètres) ~1.5s. SafetyDistance min ~3m.
- Pour les **deadlocks d'intersection** (deux voitures qui s'entrebloquent), prévoir un timeout : si une voiture attend > 10s en queue, elle prend le passage de force. Edge case rare avec FIFO mais utile en filet.
- Si Phase 5 (pathfinding A*) est faite côté piéton, on peut considérer un pathfinder voiture **sur le lane graph** (Dijkstra sur les connexions). Permet aux voitures de viser une destination spécifique. Optionnel V13, naturel en V13.5.

---

## Phase 14 — Symbiose piétons ↔ voitures

**Statut : `[ ] TODO`**

### But fonctionnel

Le moment où les deux populations se voient. Coordination bidirectionnelle aux passages piétons :

- **Piétons** : ne marchent dans une `RoadZone` que via un `CrosswalkZone`. Vérifient le crosswalk avant de s'engager : sûr (pas de voiture proche / feu vert) → traverse ; sinon → attend au bord.
- **Voitures** : détectent les `CrosswalkZone` devant elles. Si un piéton est sur le crosswalk OU s'apprête à y entrer (intent visible), freinent à la ligne d'arrêt. Repartent quand le crosswalk est libre.
- **Feux** (optionnel V14, requis V14.5) : les `CrosswalkZone` `Timed` alternent vert piéton / vert voiture sur un cycle. Pendant vert piéton : voitures s'arrêtent inconditionnellement, piétons traversent. Pendant vert voiture : inverse.

### Données

**Extension `CrosswalkZone`** :

```csharp
public struct CrosswalkOccupancy : IComponentData
{
    public int PedestriansOnIt;
    public int PedestriansWaiting;       // côté piéton, intent de traverser
    public float SignalTimer;            // pour SignalType == Timed
    public CrosswalkPhase CurrentPhase;
}

public enum CrosswalkPhase : byte
{
    PedestrianGreen = 0,    // piétons traversent, voitures stoppent
    PedestrianRed = 1,      // voitures roulent, piétons attendent
}
```

**Nouveau composant piéton** :

```csharp
public struct PedestrianCrossingIntent : IComponentData
{
    public Entity TargetCrosswalk;
    public byte HasIntent;
}
```

Posé par `AgentGoalSystem` (Phase 4) quand la trajectoire calculée traverserait une `RoadZone` ; le steering route alors l'agent vers le crosswalk le plus proche au lieu de traverser direct.

### Systèmes

- **`CrosswalkSignalSystem.cs`** — single-thread (mutations partagées sur `CrosswalkOccupancy`). Met à jour les phases pour les crosswalks `Timed`, basé sur `SignalCycleDuration` et `SignalPhaseOffset`. Pour `AlwaysGreen`, phase reste `PedestrianGreen` en permanence.
- **`CrosswalkOccupancyTrackerJob`** — au début de frame, recompte les `PedestriansOnIt` en parcourant les agents et testant l'inclusion dans chaque crosswalk via le spatial index. ScheduleParallel possible si on accumule via atomics ou via une passe single-thread post-reduce.
- **`CarYieldSystem.cs`** — `[BurstCompile]`. Pour chaque voiture, regarde le ou les crosswalks dans son cône avant (proche, ~20m). Si l'un est en phase `PedestrianGreen` OU `PedestriansOnIt > 0` → `TargetSpeed = 0` (arrête à la ligne, calculée comme bord du crosswalk côté approche). Sinon `TargetSpeed` reste celui de `CarLaneFollowingSystem`.
- **`PedestrianCrossingSystem.cs`** — pour chaque agent qui s'approche d'un crosswalk :
  - Si trajectoire prévue traverse une route et `HasIntent == 0` → set `TargetCrosswalk`, set `HasIntent = 1`, oriente `desired` vers l'entrée du crosswalk (et non la destination finale).
  - Si déjà sur le crosswalk → traverse normalement, accélère légèrement (l'agent veut passer rapidement).
  - Au bord, si phase == `PedestrianRed` → décélère à l'approche, attend.

### Modifications corollaires

- **`AgentSteeringSystem.cs`** : nouvelle source de `desired` : `PedestrianCrossingIntent.TargetCrosswalk` quand `HasIntent == 1`. Priorité dans l'arbre des decision : `Interacting > CrossingIntent > Traveling (goal) > PathFollower > Stationary wander`.
- **`AgentGoalSystem.cs`** : détecter quand le trajet ligne droite vers le POI traverse une `RoadZone`. Si oui, poser un `PedestrianCrossingIntent`. Au passage au-dessus du crosswalk, retirer l'intent et reprendre `Traveling` direct vers le POI.

### Critère de validation

- [ ] Une voiture qui approche un crosswalk avec un piéton dessus → ralentit progressivement, s'arrête à la ligne, repart quand le piéton est passé.
- [ ] Un piéton dont le POI est de l'autre côté de la route → dévie vers le crosswalk au lieu de traverser direct.
- [ ] Crosswalk `Timed` cycle ~10s : alternance visible. Voitures et piétons attendent leur tour, pas de superposition.
- [ ] Stress test : 5000 piétons + 100 voitures + 8 crosswalks. Pas d'accident visuel (aucune superposition voiture-piéton en mouvement). FPS ≥ 50.
- [ ] Edge case : deux piétons traversent dans des directions opposées sur le même crosswalk → se croisent normalement (la séparation + ORCA-lite de Phase 3 marche aussi sur le crosswalk).

### Notes pour reprise

- Le **`PedestrianCrossingIntent` est l'élément clé**. Sans lui, les piétons ne planifient pas la traversée et déclenchent constamment le pushout de `RoadZone`, ce qui produit du jitter. Avec, ils convergent proprement vers le crosswalk avant de l'aborder.
- Le `CarYieldSystem` regarde le **ou les** crosswalks dans le cône — important si une route a plusieurs crosswalks rapprochés. Prendre le plus proche dont l'état exige arrêt.
- La **ligne d'arrêt** (où la voiture doit stopper) n'est pas le centre du crosswalk, mais son bord côté approche. Calculer comme : `center - HalfExtents.x * forwardDir + safetyMargin`. Documenter.
- Cas pathologique à éviter : un piéton qui s'arrête PILE sur le crosswalk (interaction sociale ou collision avec un autre piéton). Toute voiture le verrait comme `PedestriansOnIt > 0` indéfiniment → deadlock. Filet de sécurité : si un crosswalk est occupé > 30s sans variation, override en `PedestrianRed` pendant 5s pour évacuer.

---

## Phase 15 — Diversité véhiculaire (bus, vélos, urgence)

**Statut : `[ ] TODO`**

### But fonctionnel (optionnel, futur)

Pousser la simulation au-delà du "voitures + piétons standard" :

- **Bus** : véhicule volumineux qui s'arrête aux POIs `BusStop`. Au lieu de juste passer, il occupe l'arrêt 10-20s, pendant lesquelles des agents proches peuvent "monter" (despawn de l'agent + signal sur le bus pour modèle de file plus tard). Lien fort avec Phase 4 (POI BusStop) et Phase 8 (despawn).
- **Vélos** : véhicule hybride. Peut rouler sur `RoadZone` (comme une voiture lente) ou sur des `BikeLane` dédiées (nouvelle zone). Vitesse intermédiaire (5-7 m/s). Plus maniable qu'une voiture (sépare des piétons s'il dérive sur un trottoir).
- **Véhicules d'urgence** (ambulance, police, pompiers) : priorité absolue. Les autres voitures se rangent sur le côté (`TargetSpeed = 0` + petit offset latéral). Les piétons cèdent (similaire à un `Stimulus` Phase 9). Active une sirène = `StimulusEvent` de type `EmergencyApproach`.

### Périmètre estimatif

Cette phase est volontairement laissée floue. Chaque sous-feature est un mini-projet :

- **Bus** : ~1 jour, dépend de Phase 8 si on veut le "boarding" propre (sinon les agents disparaissent juste à l'approche).
- **Vélos** : ~0.5-1 jour, principalement une variante de voiture avec params différents + nouvelle zone optionnelle.
- **Urgence** : ~1 jour, demande Phase 9 (stimulus) pour la propagation de l'événement.

### Critère de validation (par sous-feature)

À définir au moment d'attaquer la phase. Ne pas pré-planifier en détail.

### Notes pour reprise

- Ne pas attaquer Phase 15 avant que 11-14 soient solides en production. Les véhicules spéciaux exposent les fragilités de l'IA de base — autant les régler d'abord.
- Si l'urgence est le besoin (ex: démo "ville évacuée"), considérer un fast-path : juste l'ambulance avec son stimulus, sans bus ni vélo.

---

# Procédure de reprise (pour une nouvelle session Claude)

Quand tu reprends après une coupure :

1. **Lis ce fichier** en entier pour situer où on en est.
2. **Lis `RAPPORT_DEVELOPPEMENT_IA.md`** pour l'historique complet (Phase 0 et au-delà côté rendu/VAT).
3. **Regarde la table "Vue d'ensemble des phases"** ci-dessus pour identifier la prochaine phase à attaquer.
4. **Lis la phase ciblée** en détail, et vérifie dans le code si des composants/systèmes mentionnés existent déjà (cas où la phase est `[~] EN COURS`).
5. **Demande confirmation à l'utilisateur** avant de commencer l'implémentation, pour valider qu'on est bien sur la bonne phase.

# Procédure de clôture d'une phase

Quand une phase est livrée :

1. Mettre son statut à `[x] FAIT — <date YYYY-MM-DD> — commit <hash court>`.
2. Sous "Critère de validation", ajouter une section **"Livré"** qui résume :
   - Ce qui marche concrètement
   - Les fichiers créés/modifiés
   - Les écarts vs plan initial (renommages, choix d'archi, simplifications)
3. Mettre à jour la table "Vue d'ensemble" en haut.
4. Si la phase a changé une convention globale (nouveau pattern, nouveau singleton), l'ajouter dans la section "Conventions à respecter" du point de départ.
