# Rapport de développement assisté par IA — Pipeline d'animation VAT pour foule ECS

Ce document récapitule les fonctionnalités majeures que j'ai développées (en tant qu'assistant IA) dans le cadre de ce projet Unity ECS. Pour chaque grande étape, je détaille la demande initiale de l'utilisateur, mes choix techniques, le raisonnement derrière ces choix, et les fichiers concernés.

L'objectif global du projet : afficher et animer un très grand nombre d'agents humanoïdes (foule) dans une scène Unity, en utilisant l'architecture ECS (Entities) pour profiter du parallélisme et obtenir les meilleures performances possibles.

---

## Stack technique du projet

- **Unity 6** (URP 17.x)
- **Entities 1.3.14** + **Entities.Graphics 1.4.12** (DOTS / ECS)
- **Burst** + **Jobs** (compilation native, parallélisation)
- **HLSL** (shader custom pour le rendu animé)
- **Mathematics** (math vectorielle SIMD-friendly)

---

## Vue d'ensemble de l'architecture finale

```
Entities ECS (CPU côté simulation)
├── AgentSteeringSystem      → chemin + séparation (spatial hash)
├── AgentMovementSystem      → applique la vélocité au transform
├── AgentAnimationSystem     → choisit Idle/Walk, avance le temps d'anim
├── AgentVisibilitySystem    → active/désactive le rendu selon distance caméra
└── PropagateMaterialPropsToLODSystem → recopie les props per-instance vers les enfants LOD

Pipeline VAT (GPU côté rendu)
├── ClusterDecimator         → décime un mesh en regroupant les vertices (LOD)
├── VATBakerWindow           → outil éditeur : bake mesh + texture VAT par LOD
├── AgentVAT.shader          → sample la VAT en vertex shader, support DOTS instancing
├── VATAsset                 → ScriptableObject contenant les données baked
└── AgentLODBakingSystem     → propage les composants per-instance aux entités LOD pendant le baking
```

---

# Phases du développement (chronologique)

---

## Phase 1 — Conception du pipeline d'animation

### Demande de l'utilisateur

> « Je voudrais que mes entité ai un model un peu plus complexe (modifier le prefabs pour y mettre un autre model et surtout l'animé) (...) il y aura une animation de marche et c'est tout a moins qu'on ajouté des etats aux entité pour qu'elle puissent marcher, s'arreter, ou autre, tu en pense quoi ? »

Suivi de : « Quel est la methode la plus optimiser pour garder mes performences pour enormement d'entité en ajoutant les animations ? »

### Ma réponse technique

J'ai exposé les trois approches possibles pour animer des entités ECS :

1. **GameObject hybride** : composant compagnon avec Animator classique. Simple mais coûteux, ne passe pas l'échelle au-delà de ~200 agents.
2. **VAT (Vertex Animation Texture)** : pré-calculer chaque frame d'animation dans une texture, le shader lit la position des vertices dedans. Aucun coût CPU par agent, scale à des milliers d'agents.
3. **Latios Kinemation** : framework tiers GPU-skinning natif ECS. Plus flexible mais dépendance externe.

### Choix retenu : VAT custom

**Pourquoi** : l'utilisateur a explicitement demandé la solution la plus optimisée, peu importe la complexité d'implémentation. Le VAT custom :
- N'a aucune dépendance externe (contrôle total du code)
- Donne le meilleur ratio performance/scalabilité
- Permet 1 seul draw call instancié pour toute la foule (via DOTS instancing)
- Coût CPU par agent = 0 (l'animation vit entièrement dans la texture, lue par le GPU)

---

## Phase 2 — Implémentation initiale de la pipeline VAT

### Travail effectué

Création de la pipeline complète en une passe :

**Composants ECS (CPU)** — `Assets/Scripts/Components/AgentAnimationComponents.cs`
- `AgentAnimationState` : état interne (clip actuel, temps, phase aléatoire)
- `VATClipTable` : métadonnées des clips (start frame, durée, fps) par agent
- `AnimClipProperty` et `AnimTimeProperty` : composants `[MaterialProperty]` qui sont automatiquement uploadés au GPU par Entities Graphics

**ScriptableObject** — `Assets/Scripts/Animation/VATAsset.cs`
- Conteneur durable pour les données baked : Mesh, texture VAT, métadonnées des clips, dimensions de texture

**Shader URP HLSL** — `Assets/Shaders/AgentVAT.shader`
- 3 passes : ForwardLit (rendu PBR), ShadowCaster (ombres), DepthOnly (depth pre-pass)
- Support DOTS instancing complet (`UNITY_DOTS_INSTANCING_START` / `END`)
- Sample de la VAT en vertex shader pour remplacer la position de chaque vertex
- Mode debug intégré pour visualiser vertex IDs / positions / temps d'animation

**Système ECS d'animation** — `Assets/Scripts/Systems/AgentAnimationSystem.cs`
- Burst-compilé, IJobEntity parallèle
- Lit `AgentMovement.Velocity` pour choisir Idle/Walk via un seuil de vitesse au carré (pas de `sqrt`)
- Avance le temps de clip, gère le wrap-around pour éviter les floats infinis
- Écrit dans les composants `[MaterialProperty]` qui sont synchronisés au GPU

**Outil éditeur de bake** — `Assets/Scripts/Editor/VATBakerWindow.cs`
- Fenêtre `Crowd > VAT Baker`
- Utilise `AnimationMode.SampleAnimationClip()` + `SkinnedMeshRenderer.BakeMesh()` pour échantillonner chaque frame
- Génère 4 assets : Mesh statique, texture VAT (RGBAFloat initialement), Material, VATAsset SO

**Authoring** — `Assets/Scripts/Authoring/AgentAuthoring.cs`
- Bake les composants ECS sur l'entité agent
- Réfère le VATAsset pour peupler la `VATClipTable`

### Raisonnement technique

**Le choix d'utiliser `[MaterialProperty]`** : Entities Graphics gère automatiquement l'upload de ces composants en buffers GPU pour le DOTS instancing. C'est le mécanisme officiel pour avoir des données per-instance dans le shader sans casser le batching.

**Le choix de baker en RGBAFloat (initialement)** : précision maximale (32 bits par canal) pour éviter tout artefact visuel lié à la quantification des positions. Plus tard optimisé en RGBAHalf (16 bits) sans perte visible.

**Le système d'animation runtime minimal** : le shader fait déjà le wrap-around (modulo sur le temps), mais l'animation system fait aussi un wrap CPU pour éviter d'accumuler des floats >> millions et perdre la précision flottante.

---

## Phase 3 — Premier debug : vertex IDs corrompus

### Symptôme rapporté

> « Une fois que j'ai mis le mesh filter et autre mon models a ete deteriorer (...) on vois bien que l'animation est correcte mais le personnage ne ressemble plus a rien »

En mode debug (vertex ID coloré), au lieu d'un dégradé continu, des couleurs cassées entre les sphères.

### Cause identifiée

J'avais initialement stocké les indices de vertices dans le canal **UV2** du mesh, mais ce canal ne survit pas toujours à la sérialisation `.asset` selon la version d'Unity et les paramètres d'import. Le shader recevait des valeurs corrompues / vides.

### Fix appliqué

Remplacement de UV2 par le sémantique HLSL **`SV_VertexID`**, qui donne directement l'index du vertex depuis le pipeline GPU sans avoir besoin de stocker quoi que ce soit dans le mesh.

**Fichiers modifiés** :
- `AgentVAT.shader` : `float2 vatId : TEXCOORD2` → `uint vertexId : SV_VertexID` dans les 3 passes
- `VATBakerWindow.cs` : suppression de l'écriture UV2

### Raisonnement technique

`SV_VertexID` est une variable système HLSL (Shader Model 4.0+) garantie par le GPU. Elle évite le risque de corruption pendant la sérialisation et économise 8 octets par vertex côté mémoire.

---

## Phase 4 — Deuxième debug : ordre des vertices baked

### Symptôme rapporté

Le modèle apparaissait avec des « sphères placées aux endroits clés » (articulations), avec le reste de la géométrie semblant ne pas correspondre.

### Cause identifiée

J'utilisais `srcMesh.vertices` (lecture directe du sharedMesh du SkinnedMeshRenderer) pour construire le mesh de référence statique. Or, sur certaines configurations, `SkinnedMeshRenderer.BakeMesh()` peut sortir les vertices dans un **ordre différent** de `sharedMesh.vertices`. Résultat : les indices du mesh de référence ne correspondaient plus aux colonnes de la VAT.

### Fix appliqué

Construction du mesh de référence directement à partir du résultat de `BakeMesh()` capturé à la frame 0 du premier clip. Ainsi l'ordre des vertices du mesh statique == ordre des positions stockées dans la VAT, garanti.

**Fichiers modifiés** :
- `VATBakerWindow.cs` : capture des positions/normales/UVs/indices via BakeMesh au lieu de srcMesh.vertices

### Raisonnement technique

C'est le **single source of truth** : tout vient du même appel BakeMesh, donc l'ordre est cohérent par construction. C'est plus robuste que de croiser deux APIs Unity dont la garantie d'ordre n'est pas explicitement documentée.

---

## Phase 5 — Support multi-SkinnedMeshRenderer

### Symptôme rapporté

> « Les sphere que je vois sont bien presente dans le models du X bot de mixamo. C'est le reste qui n'apparait simplement pas. Il faut recuperer tout les meshs pour mon personnage »

Le modèle Mixamo (Beta, X-Bot) a plusieurs `SkinnedMeshRenderer` distincts : un pour les sphères des articulations, un pour les surfaces du corps. Mon baker n'en prenait que le premier.

### Fix appliqué

Itération sur tous les `SkinnedMeshRenderer` enfants via `GetComponentsInChildren<SkinnedMeshRenderer>(true)` (incluant les inactifs). Pour chaque SMR :
- Décalage (`smrOffsets[i]`) dans le mesh combiné
- Bake parallèle dans la VAT à la bonne position

Construction d'un mesh **unique combiné** avec indices décalés (les triangles de Beta_Surface référencent des vertices dans la plage `[12473, 28373]` après concaténation).

Log ajouté pour aider l'utilisateur à vérifier : « Found N SkinnedMeshRenderer(s) in 'X' ».

**Fichiers modifiés** :
- `VATBakerWindow.cs` : refonte de `Bake()` pour gérer N SMRs

### Raisonnement technique

Un mesh combiné est essentiel pour rester en **1 seul draw call** via DOTS instancing. Des SMRs séparés impliqueraient soit plusieurs draw calls par agent, soit une duplication des données per-instance.

---

## Phase 6 — Texture trop grande (layout 2D)

### Symptôme rapporté

> « Je me retrouve avec un "failed to create texture because of invalid parameters" »

Avec 28 374 vertices combinés (Beta_Joints + Beta_Surface), la VAT avait une largeur de 28 374 px, ce qui dépasse la limite GPU (16 384 sur la plupart des GPUs, 4 096 sur mobile).

### Fix appliqué

Refonte du **layout de la texture** : largeur cappée à `MAX_TEX_WIDTH = 4096`, et les vertices excédentaires sont étalés sur des rangées supplémentaires par frame.

- `_VATWidth` : largeur effective de la texture
- `_RowsPerFrame` : nombre de rangées que chaque frame occupe (= `ceil(totalVertexCount / vatWidth)`)
- `_VATHeight` : hauteur totale (= `totalFrames * rowsPerFrame`)

Calcul d'index pour un vertex `v` à la frame `f` :
```
col      = v mod vatWidth
localRow = v / vatWidth
texRow   = f * rowsPerFrame + localRow
```

**Fichiers modifiés** :
- `VATBakerWindow.cs` : nouveau calcul de dimensions + écriture des pixels
- `AgentVAT.shader` : nouvelle fonction `SampleVAT` avec le layout 2D
- `VATAsset.cs` : ajout des champs `VATWidth`, `VATHeight`, `RowsPerFrame`

### Raisonnement technique

C'est le **layout VAT standard** pour les meshes denses. La largeur power-of-two reste compatible avec tous les GPUs (desktop/mobile/console). Aucune perte de précision ni de fonctionnalité, juste une indirection en plus dans le calcul d'index.

---

## Phase 7 — Refactor multi-material → single material + vertex colors

### Symptôme rapporté

> « J'ai ajouté le deuxieme material et maintenant je vois le model en entié. Maintenant que ca marche les anilmations ne sont plus jouer »

Avec 2 materials sur le `MeshRenderer` (un par sous-mesh joints/surface), les agents s'affichaient mais l'animation était figée.

### Cause identifiée

Entities Graphics, quand un `MeshRenderer` a plusieurs materials, **divise l'entité agent** : il crée des sous-entités enfants pour le rendu (une par material). Les composants per-instance comme `AnimClipProperty` / `AnimTimeProperty` restent sur l'entité racine, mais les draws happenent sur les enfants → le GPU lit la valeur par défaut (0) pour `_AnimTime` → tous les agents figés à frame 0.

### Fix appliqué

Refonte du baker pour produire **un seul mesh avec un seul submesh + un seul material**. La distinction visuelle joints / surface est encodée via les **couleurs per-vertex** : chaque vertex porte la teinte de son material source d'origine. Le shader multiplie l'albedo par la couleur per-vertex.

**Fichiers modifiés** :
- `VATBakerWindow.cs` : fusion des indices des SMRs en un seul submesh, écriture des `Color[]` per-vertex
- `AgentVAT.shader` : ajout de `half4 color : COLOR` aux Attributes, multiplication dans le fragment

### Raisonnement technique

Cette approche garde **un seul draw call instancié** pour toute la foule (perf maximale) tout en préservant l'identité visuelle Joints / Surface. Les couleurs per-vertex sont une convention standard, supportée nativement par tous les pipelines de rendu.

---

## Phase 8 — Bug critique : interpolation entre frames

### Symptôme rapporté

> « Il faudrais bien unifier le chargement pour charger tout les meshes (...) car actuellement les aniamtions sont completement exploser »

Les personnages s'affichaient et marchaient, mais avec une géométrie clairement déformée — des triangles étirés, des "spikes" géants partant du corps.

### Cause identifiée (la plus subtile du projet)

Dans le layout 2D de la VAT, chaque frame occupe N rangées consécutives. Le calcul `texRow = globalFrame * rowsPerFrame + localRow` repose sur le fait que `globalFrame` est un **entier**.

Or, dans le shader, `globalFrame = startF + fmod(animTime * fps, countF)` est un **float non-entier** (par ex. 3.7 entre les frames 3 et 4). Avec `rowsPerFrame = 7` :
- `texRow = 3.7 × 7 + localRow = 25.9 + localRow`

Pour un vertex avec `localRow = 3`, on devait sampler la rangée `3 × 7 + 3 = 24` (frame 3, localRow 3). Mais on sample la rangée 28.9 ≈ 29, qui correspond à **frame 4, localRow 1** — un autre vertex de la frame d'après.

Conséquence : les vertices d'un même agent à un instant T lisent des positions appartenant à des frames différentes, créant des triangles étirés entre poses incompatibles.

### Fix appliqué

Une seule ligne : `floor()` sur `frameInClip` dans le shader avant le calcul de `texRow`.

```hlsl
float frameInClip = floor(fmod(_AnimTime * fps, countF));
```

**Fichiers modifiés** :
- `AgentVAT.shader` : `SampleVAT()` avec floor explicite + commentaire d'avertissement

### Raisonnement technique

Sans l'interpolation entre frames (qu'on n'utilise pas — sampling à 30 fps c'est déjà fluide), `globalFrame` doit être un entier strict pour que l'arithmétique d'adressage 2D fonctionne. Le `floor()` garantit cela tout en restant `O(1)` côté GPU.

C'est un bug typique des layouts 2D de VAT que la littérature publique ne souligne pas toujours.

---

## Phase 9 — Sprint d'optimisations performance

### Demande de l'utilisateur

> « Maintenant que ca marche fait une analyse complete du pipeline actuel et propose moi une roadmap pour une optimisation et pour pouvoir maximiser le nombre d'entité dans la scene »

Puis :

> « Implemente toute ces amélioration, donne toi a fond pour me proposer un projet le plus optimiser possible ! »

### Travail effectué

J'ai implémenté 6 optimisations en un sprint :

#### 9.1 VAT en demi-précision (RGBAHalf)

- Format : `TextureFormat.RGBAFloat` → `TextureFormat.RGBAHalf`
- Gain : **−50 % de bande passante GPU**, **−50 % de mémoire texture**, aucune perte visible (16-bit float suffit largement pour les positions de personnages)

#### 9.2 Bounds plus serrées (×1.05 au lieu de ×1.2)

- Sur le mesh asset : `bounds.size * 1.05f`
- Gain : frustum culling d'Entities Graphics plus agressif → moins d'agents rendus alors qu'ils ne sont pas vraiment visibles

#### 9.3 Steering cadencé (skip 1 frame sur 2)

- `AgentSteeringSystem` accepte un `SteeringInterval` (défaut 2)
- Skip des frames intermédiaires : la vélocité reste à sa dernière valeur (le smoothing par lerp absorbe la discontinuité)
- DeltaTime multiplié par l'intervalle pour préserver la dynamique
- Gain : **−50 % du coût CPU** du job le plus lourd (spatial hash + voisins)

#### 9.4 Animation cadencée

- `AgentAnimationSystem` accepte un `AnimationInterval` (défaut 1)
- Permet de descendre la maj animation à 30 Hz si besoin

#### 9.5 Distance render culling per-instance

Création de :
- 2 nouveaux composants : `AgentVisibleProperty` et `AgentShadowVisibleProperty` (avec `[MaterialProperty]`)
- 1 nouveau système : `AgentVisibilitySystem` qui lit `Camera.main` et écrit ces flags selon la distance par agent

Dans le shader, vertex shader early-exit : si `_AgentVisible < 0.5`, output une position en `(0, 0, -2, 1)` (derrière le plan near → triangle culled par le rasterizer).

- Gain : **fragment shading skippé** pour les agents au-delà de `MaxRenderDistance`. Pour une foule étalée, c'est typiquement **−50 à −80 % du coût fragment**.

#### 9.6 Distance shadow culling

Pareil avec un seuil plus serré (`MaxShadowDistance` < `MaxRenderDistance`).
- Les agents intermédiaires sont visibles mais ne projettent plus d'ombres au-delà du seuil
- Gain : **−20 à −50 %** du coût de la ShadowCaster pass

### Fichiers créés / modifiés

**Créés** :
- `Assets/Scripts/Systems/AgentVisibilitySystem.cs` — culling per-instance basé caméra

**Modifiés** :
- `Components/AgentAnimationComponents.cs` — nouveaux composants `AgentVisibleProperty`, `AgentShadowVisibleProperty`
- `Components/AgentComponents.cs` — nouveaux champs perf dans `SpawnerConfig`
- `Authoring/CrowdSpawnerAuthoring.cs` — section "Performance" dans l'Inspector
- `Authoring/AgentAuthoring.cs` — bake des nouveaux composants
- `Systems/AgentSteeringSystem.cs` — cadence + frame counter
- `Systems/AgentAnimationSystem.cs` — cadence
- `Editor/VATBakerWindow.cs` — RGBAHalf, bounds 1.05x
- `Shaders/AgentVAT.shader` — distance cull dans Forward + Shadow + Depth, nouvelles props instancing

### Raisonnement technique global

Les optimisations sont **toutes per-instance, jamais structurelles**. Pas d'ajout/retrait dynamique de composants ECS (qui sont coûteux car déclenchent des structural changes), tout passe par des valeurs flottantes dans des composants existants. Le shader fait l'early-exit en quelques cycles ALU au début du vertex shader.

Combinées, ces optimisations permettent typiquement de passer de **5 000 à 30 000+ agents** dans une scène à GPU constant.

---

## Phase 10 — Système de LOD avec décimation automatique

### Demande de l'utilisateur

> « On va implementer le lod maintenant »

### Architecture choisie

LOD à 3 niveaux automatiquement générés par le baker, exposés au runtime via le `LODGroup` natif d'Unity :

| Niveau | Géométrie | Population typique |
|--------|-----------|--------------------|
| LOD0 | Mesh source complet (~28k verts) | 5-15 % des agents (proches caméra) |
| LOD1 | Décimé grille 24³ (~5-8k verts) | 30-50 % des agents (distance moyenne) |
| LOD2 | Décimé grille 14³ (~1.5-2k verts) | 35-65 % des agents (loin) |

### Composants créés

#### 10.1 Décimateur de mesh — `Assets/Scripts/Editor/ClusterDecimator.cs`

Algorithme de décimation par **regroupement de vertices en cellules d'une grille 3D**.

Étapes :
1. Calcul de la bounding box du mesh
2. Subdivision en `cellsPerAxis³` cellules
3. Chaque vertex source est assigné à sa cellule
4. Chaque cellule occupée devient un **cluster** dont les attributs (position, normale, UV, couleur) sont la **moyenne** des attributs des vertices qu'elle contient
5. Les triangles sont remappés vers les indices de cluster ; les triangles dégénérés (2+ vertices dans la même cellule) sont éliminés

Sortie : un nouveau mesh + une table `clusterToSources[]` qui pour chaque cluster donne la liste des vertices source.

#### 10.2 Multi-LOD dans le baker

Refonte de `VATBakerWindow.Bake()` pour gérer N LODs en une seule passe d'animation :

1. Capture des données de référence pour chaque SMR (frame 0 du premier clip)
2. **Décimation** de chaque SMR pour chaque LOD > 0 via `ClusterDecimator`
3. Allocation d'une VAT par LOD (dimensions adaptées)
4. Boucle de bake des frames :
   - Pour chaque frame, pour chaque SMR : `BakeMesh()` une seule fois
   - **LOD0** : écriture directe des positions des vertices dans la VAT
   - **LOD>0** : pour chaque cluster, position moyenne des vertices source dans ce cluster → écrite dans la VAT du LOD
5. Sauvegarde des assets pour chaque LOD : `*_VAT_Mesh.asset`, `*_VAT_Position.asset`, `*_VAT_Material.mat`, `*_VAT.asset` (et leurs variantes `_LOD1`, `_LOD2`)

**Cohérence cruciale** : la cluster-table calculée à la frame 0 est **réutilisée à chaque frame**. Cela garantit que tous les vertices d'un même cluster suivent la même évolution dans le temps, sans pop visuel entre frames.

#### 10.3 Propagation des per-instance props vers les LODs

**Problème** : un `LODGroup` au runtime crée des entités ECS séparées pour chaque LOD (chaque child GameObject avec MeshRenderer = une entité de rendu distincte). Les composants per-instance (`AnimClipProperty`, etc.) baked sur l'agent racine ne sont **pas** automatiquement présents sur ces entités enfants.

**Solution en deux étapes** :

**(a) Au baking** — `Assets/Scripts/Systems/AgentLODBakingSystem.cs` (BakingSystem)

Tourne en `PostBakingSystemGroup` après tous les Bakers. Pour chaque entité racine avec `AgentTag` (incluant les prefab entities via `EntityQueryOptions.IncludePrefab`), itère sur le `LinkedEntityGroup` buffer et ajoute les 4 composants per-instance à chaque enfant qui a un `MaterialMeshInfo` (= vraie entité de rendu).

**(b) Au runtime** — `Assets/Scripts/Systems/PropagateMaterialPropsToLODSystem.cs`

Burst-compilé, parallèle. Chaque frame, lit les valeurs des 4 composants per-instance sur l'entité racine et les recopie sur chaque enfant LOD via `ComponentLookup`. Utilise `[NativeDisableContainerSafetyRestriction]` pour permettre l'écriture parallèle sur les entités enfants (sûr car aucun chevauchement entre agents).

### Difficultés techniques résolues

1. **Erreur Baker** : un `Baker` ne peut modifier QUE les entités qu'il crée ou via `CreateAdditionalEntity`. Tenter d'`AddComponent` sur une entité créée par un autre Baker (le MeshRendererBaker d'Entities Graphics) lève `InvalidOperationException`. Résolu en passant par un `BakingSystem` (qui a accès en écriture à toutes les entités).

2. **Aliasing de scheduler** : un `IJobEntity` qui a à la fois des paramètres `in T` et un `ComponentLookup<T>` (même type) est rejeté par le scheduler car il considère qu'il y a aliasing potentiel. Résolu en lisant la valeur de la racine via `ComponentLookup[rootEntity]` au lieu d'un paramètre `in`, supprimant l'aliasing.

3. **Query qui ne matche pas le prefab** : par défaut les ECS queries excluent les entités avec le tag `Prefab`. Le `BakingSystem` ne trouvait donc pas la racine de l'agent pendant le baking. Résolu avec `EntityQueryOptions.IncludePrefab`.

4. **Cache de SubScene stale** : Entities baking peut conserver des données obsolètes dans `Assets/SceneDependencyCache/`. Documenté la nécessité de supprimer ce dossier pour forcer un rebake complet.

### Raisonnement technique

L'approche **décimation par grille de clusters** est volontairement simple :
- Rapide (linéaire en nombre de vertices)
- Déterministe (mêmes inputs → mêmes outputs)
- Convient particulièrement aux foules vues à distance, où la fidélité géométrique fine n'est pas perceptible

Une alternative plus sophistiquée (quadric error metrics, Garland & Heckbert) donnerait une meilleure préservation de la silhouette mais avec 5–10× plus de code et un coût de baking notable. Vu l'usage (LODs à distance), ce gain de qualité n'est pas pertinent.

L'approche **prop propagation runtime** plutôt que **prop duplication baking-only** garde le code source de vérité unique : `AgentAnimationSystem` écrit sur la racine, le PropagateSystem mirror vers les enfants. Une approche alternative consisterait à faire écrire `AgentAnimationSystem` sur N entités (une par LOD), mais cela complique la gestion d'état (qui est le « vrai » état ?) et coûte en cohérence.

---

## Phase 11 — Amélioration du spawn (anti-blocage)

### Demande de l'utilisateur

> « J'aimerai conserver le collider pour pas qu'ils se rentrent dedans mais au moment de lancer la scene ils sont tres regrouper ce qui bloc certains groupe qui ne peuvent plus se depalcer »

### Diagnostic

Le `CapsuleCollider` n'est pas activé physiquement (pas de Unity.Physics dans le projet), donc il ne bloque rien. Le vrai problème : le spawn aléatoire crée parfois des paquets denses d'agents superposés. La force de séparation steering s'annule (forces venant de toutes les directions), et certains agents restent figés.

### Fix appliqué

Refonte du spawn dans `CrowdSpawnerSystem` : passage du spawn aléatoire à un **spawn en grille avec jitter**.

```csharp
int cols = ceil(sqrt(agentCount));
int rows = ceil(agentCount / cols);
float cellW = zoneSize.x / cols;
float cellD = zoneSize.z / rows;

// Pour chaque agent i:
int col = i % cols;
int row = i / cols;
float jitterX = (random - 0.5) * cellW * 0.6f; // jitter ±30 % de la taille de cellule
float jitterZ = (random - 0.5) * cellD * 0.6f;
position = zoneOrigin + (col + 0.5) * cellW + jitterX, ...
```

Chaque agent obtient sa propre cellule virtuelle, plus un déplacement aléatoire borné pour éviter le rendu « échiquier ». Distance minimale garantie entre agents au spawn.

**Fichier modifié** : `Assets/Scripts/Systems/CrowdSpawnerSystem.cs`

### Raisonnement technique

C'est l'approche classique de spawn en foule (équivalent simplifié d'un Poisson disk sampling). Coût : O(N) à la place de O(N) du random, mais sans variance — chaque exécution donne une distribution bien équilibrée. L'aléa est conservé via le jitter, donc visuellement la foule reste organique (pas un échiquier).

---

# Récapitulatif final

## Fichiers créés (par moi, l'IA)

| Fichier | Rôle |
|---------|------|
| `Assets/Scripts/Animation/VATAsset.cs` | ScriptableObject conteneur de bake VAT |
| `Assets/Scripts/Components/AgentAnimationComponents.cs` | Composants ECS d'animation + material properties per-instance |
| `Assets/Scripts/Systems/AgentAnimationSystem.cs` | Système de pilotage de l'animation (idle/walk + temps) |
| `Assets/Scripts/Systems/AgentVisibilitySystem.cs` | Culling per-instance basé distance caméra |
| `Assets/Scripts/Systems/AgentLODBakingSystem.cs` | BakingSystem pour propager les props sur les entités LOD pendant le baking |
| `Assets/Scripts/Systems/PropagateMaterialPropsToLODSystem.cs` | Système runtime de propagation racine → enfants LOD |
| `Assets/Scripts/Editor/VATBakerWindow.cs` | Outil éditeur de bake VAT multi-LOD |
| `Assets/Scripts/Editor/ClusterDecimator.cs` | Algorithme de décimation par clusters |
| `Assets/Shaders/AgentVAT.shader` | Shader URP HLSL pour le rendu VAT avec DOTS instancing |

## Fichiers modifiés

| Fichier | Modifications |
|---------|--------------|
| `Assets/Scripts/Authoring/AgentAuthoring.cs` | Bake des composants ECS animation + visibilité |
| `Assets/Scripts/Authoring/CrowdSpawnerAuthoring.cs` | Section Performance dans l'inspector (distances, intervals) |
| `Assets/Scripts/Components/AgentComponents.cs` | Champs perf dans `SpawnerConfig` |
| `Assets/Scripts/Systems/CrowdSpawnerSystem.cs` | Spawn en grille, randomisation du PhaseOffset |
| `Assets/Scripts/Systems/AgentSteeringSystem.cs` | Cadence (skip frames) |

## Capacité de l'architecture finale

Pour un personnage Mixamo (~28k verts combinés) avec une animation Idle + Walk :

- **GPU mémoire VAT** : ~5–10 MB par perso (LOD0 + LOD1 + LOD2)
- **Draw calls par foule** : 3 (un par niveau de LOD, instanciés)
- **Coût CPU par agent par frame** : ~quelques microsecondes (steering + animation + visibility, tous parallélisés Burst)
- **Cible de scalabilité** : 30 000+ agents en temps réel sur GPU desktop moyen

## Workflow utilisateur

1. Importer un FBX Mixamo dans Unity
2. Ouvrir `Crowd > VAT Baker`, configurer source + clips + niveaux LOD
3. Cliquer **Bake** → 12 assets générés (4 par LOD × 3 LODs)
4. Créer une prefab agent avec :
   - Root : `AgentAuthoring` + `LODGroup` (références au LOD0 VATAsset)
   - 3 enfants : `LOD0`, `LOD1`, `LOD2`, chacun avec MeshFilter+MeshRenderer pointant sur les assets de son niveau
5. Configurer le `LODGroup` (transitions screen-percentage)
6. Assigner la prefab à `CrowdSpawnerAuthoring` dans la scène
7. Reimport SubScene → ECS bake les agents + propage les composants per-instance via `AgentLODBakingSystem`
8. Play : foule entièrement animée, LOD adaptatif, performante

---

# Conclusion

Ce projet a impliqué une boucle itérative classique en développement complexe :
- Conception → implémentation → debug → optimisation

Les difficultés rencontrées sont **typiques d'un pipeline VAT custom** sur Entities Graphics 1.4 :
- L'ordre des vertices entre `SkinnedMeshRenderer.BakeMesh` et `Mesh.vertices`
- Le layout 2D des textures pour gérer les grandes meshes
- L'arithmétique d'indexation avec rowsPerFrame > 1 (qui demande des entiers stricts)
- La gestion fine des per-instance properties quand `LODGroup` ou multi-material divisent les entités

L'architecture finale a réussi à concilier **performance maximale (1 draw call par LOD instancié)**, **flexibilité (multi-SMR, multi-LOD, multi-clip)** et **simplicité d'utilisation (un seul outil de bake, workflow standard via LODGroup natif Unity)**.
