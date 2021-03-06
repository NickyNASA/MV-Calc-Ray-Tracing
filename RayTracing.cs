using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RayTracing : MonoBehaviour
{
    public ComputeShader RayTracingShader;
    private RenderTexture _target;

    private Camera _camera;
    public Texture SkyboxTexture;
    private RenderTexture _converged;

    private uint _currentSample = 0;
    private Material _addMaterial;

    //[Range(1.0f, 100.0f)]
    public float _GlassRefractIndex = 1.5f;
    [Range(0.0f, 100.0f)]
    public float lightStrength = 1.0f;
    public Vector3 lightPos = new Vector3(0, 100, 0);

    private float prevIndex = 1.5f;
    public int SphereSeed;
    public Vector2 SphereRadius = new Vector2(3.0f, 8.0f);
    public uint SpheresMax = 100;
    public float SpherePlacementRadius = 100.0f;
    private ComputeBuffer _sphereBuffer;

    private static bool _meshObjectsNeedRebuilding = false;
    private static List<RayTracingObject> _rayTracingObjects = new List<RayTracingObject>();
    /* Seed = 1, Radius [5, 30], SphereMax = 10000, Sphere placement radius = 100
     * [-60, 100, -200]
     * [30, 20, 0]
     * 0.5 * sky (1.4 by default)
     * 
     * Seed = 1, Radius [5, 18], SphereMax = 500, Sphere placement radius = 150
     * [-150, 100, -150]
     * [32.4, 45, 0]
     */
    struct MeshObject{
        public Matrix4x4 localToWorldMatrix;
        public int indices_offset;
        public int indices_count;
    }

    private static List<MeshObject> _meshObjects = new List<MeshObject>();
    private static List<Vector3> _vertices = new List<Vector3>();
    private static List<int> _indices = new List<int>();
    private ComputeBuffer _meshObjectBuffer;
    private ComputeBuffer _vertexBuffer;
    private ComputeBuffer _indexBuffer;

    public static void RegisterObject(RayTracingObject obj){
        _rayTracingObjects.Add(obj);
        _meshObjectsNeedRebuilding = true;
    }

    public static void UnregisterObject(RayTracingObject obj){
        _rayTracingObjects.Remove(obj);
        _meshObjectsNeedRebuilding = true;
    }

    // Just used to pass in variables to the compute shader
    private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride)
    where T : struct{
        // Check if there is already a buffer
        if (buffer != null) {
            // If data or buffer doesn't match the given criteria, release it
            if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride) {
                buffer.Release();
                buffer = null;
            }
        }
        if (data.Count != 0) {
            // If the buffer has been released or wasn't there to
            // begin with, create it
            if (buffer == null) {
                buffer = new ComputeBuffer(data.Count, stride);
            }
            buffer.SetData(data);
        }
    }

    private void SetComputeBuffer(string name, ComputeBuffer buffer){
        if (buffer != null) {
            RayTracingShader.SetBuffer(0, name, buffer);
        }
    }
    /*
    private void RebuildMeshObjectBuffers(){
        if (!_meshObjectsNeedRebuilding) {
            return;
        }
        _meshObjectsNeedRebuilding = false;
        _currentSample = 0;
        // Clear all lists
        _meshObjects.Clear();
        _vertices.Clear();
        _indices.Clear();
        // Loop over all objects and gather their data
        foreach (RayTracingObject obj in _rayTracingObjects) {
            Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;
            // Add vertex data
            int firstVertex = _vertices.Count;
            _vertices.AddRange(mesh.vertices);
            // Add index data - if the vertex buffer wasn't empty before, the
            // indices need to be offset
            int firstIndex = _indices.Count;
            var indices = mesh.GetIndices(0);
            _indices.AddRange(indices.Select(index => index + firstVertex));
            // Add the object itself
            _meshObjects.Add(new MeshObject()
            {
                localToWorldMatrix = obj.transform.localToWorldMatrix,
                indices_offset = firstIndex,
                indices_count = indices.Length
            });
        }
        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, 72);
        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
    }*/

    private float GetRefractIndex(int material){
        switch(material){
            case 0:
                return 1.0f;
            case 1:
                return 1.5f;
            case 2:
                return 1.33f;
            default:
                print("Tried to get the refractive index of a material that does not exist");
                return 0.0f;
        }
    }

    private struct CustomMesh{

    }

    private struct Sphere{
        public Vector3 position;
        public float radius;
        public Vector3 albedo; // Amount of light that is diffusely reflected (in a random direction)
        public Vector3 specular; // Amount of light that is reflected
        public Vector3 emittance; // How much light an object puts out (glowing effect)
        public float smoothness; // Changes the alpha level for specular reflection (higher values mean shinier object)
        public int material;
    }

    /*
     * Creates a list of spheres with a random radius and colors etc. that is then passed into the shader
     */
    private void SetUpScene(){
        Random.InitState(SphereSeed);

        List<Sphere> spheres = new List<Sphere>();
        // Add a number of random spheres
        for(int i = 0; i < SpheresMax; i++){
            Sphere sphere = new Sphere();

            // Radius and radius
            sphere.radius = SphereRadius.x + Random.value * (SphereRadius.y - SphereRadius.x);
            Vector2 randomPos = Random.insideUnitCircle * SpherePlacementRadius;

            sphere.position = new Vector3(randomPos.x, sphere.radius, randomPos.y);
            // Reject spheres that are intersecting others
            foreach(Sphere other in spheres){
                float minDist = sphere.radius + other.radius;
                if(Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
                    goto SkipSphere;
            }

            //0.2f
            if(Random.value < 0.2f){
                sphere.albedo = new Vector3(1.0f, 1.0f, 1.0f);
                float s = 1.0f;
                sphere.position = sphere.position + new Vector3(0, sphere.radius, 0);
                sphere.specular = new Vector3(s, s, s);
                sphere.emittance = Vector3.zero;
                sphere.smoothness = 0;
                sphere.material = 1;
            }else{
                // Albedo and specular color
                Color color = Random.ColorHSV();
                Color light = Random.ColorHSV();
                bool metal = Random.value < 0.3f;
                sphere.albedo = metal ? Vector3.zero : new Vector3(color.r, color.g, color.b);
                sphere.specular = metal ? new Vector3(color.r, color.g, color.b) : Vector3.one * 1.0f * Random.value;
                sphere.emittance = metal ? Vector3.zero : (Random.value < 0.0f ? 2 * new Vector3(light.r, light.g, light.b).normalized : Vector3.zero);
                sphere.smoothness = metal ? 1.0f : 0.9f * Random.value;
                sphere.material = 0;
            }

            // Add the sphere to the list
            spheres.Add(sphere);
        SkipSphere:
            continue;
        }

        /*
        for(int i = 0; i < 10; i++){
            for(int j = 0; j < 10; j++){
                Sphere k = new Sphere();
                k.position = new Vector3(25*(i-5), 10, 25*(j-5));
                k.radius = 10;
                k.albedo = Vector3.zero;// new Vector3(1f, 1f, 1f);// new Vector3(80.0f / 255.0f, 210.0f / 255.0f, 255.0f / 255.0f);
                k.specular = new Vector3(1.0f, 1.0f, 1.0f);
                k.emittance = Vector3.zero;
                k.smoothness = 10;// i / 5.0f;
                k.material = 1;
                spheres.Add(k);
            }
        }*/
        /*
        k.position = lightPos;
        k.radius = 10;
        k.albedo = new Vector3(1.0f, 1.0f, 1.0f);
        k.specular = new Vector3(0.0f, 0.0f, 0.0f);
        k.emittance = new Vector3(ka, ka, ka);
        k.smoothness = 0;
        k.material = 0;
        k.position = Vector3.zero;
        k.radius = 10;
        k.albedo = new Vector3(1.0f, 1.0f, 1.0f);
        float ks = 1.0f;
        k.specular = new Vector3(ks, ks, ks);
        k.emittance = Vector3.zero;
        k.smoothness = 0.3f;
        k.material = 1;
        spheres.Add(k);*/
        // Assign to compute buffer
        _sphereBuffer = new ComputeBuffer(spheres.Count, 4 * 15);
        _sphereBuffer.SetData(spheres);
    }
    private void OnEnable(){
        _currentSample = 0;
        SetUpScene();
    }

    private void OnDisable(){
        if(_sphereBuffer != null){
            _sphereBuffer.Release();
        }
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination){
        SetShaderParameters();
        Render(destination);
    }

    private void Render(RenderTexture destination){
        InitRenderTexture();

        RayTracingShader.SetTexture(0, "Result", _target);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
        RayTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Blit the result texture to the screen
        if(_addMaterial == null){
            _addMaterial = new Material(Shader.Find("Hidden/AddShader"));
        }
        _addMaterial.SetFloat("_Sample", _currentSample);
        //Graphics.Blit(_target, destination, _addMaterial);
        Graphics.Blit(_target, _converged, _addMaterial);
        Graphics.Blit(_converged, destination);
        _currentSample++;
    }

    private void InitRenderTexture(){
        if(_target == null || _target.width != Screen.width || _target.height != Screen.height){
            // Release render texture if we already have it

            if(_target != null){
                _target.Release();
            }

            _currentSample = 0;
            _target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.enableRandomWrite = true;
            _target.Create();
        }

        if(_converged == null || _converged.width != Screen.width || _converged.height != Screen.height){
            // Release render texture if we already have it

            if(_converged != null){
                _converged.Release();
            }

            _currentSample = 0;
            _converged = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _converged.enableRandomWrite = true;
            _converged.Create();
        }
    }

    private void Awake(){
        _camera = GetComponent<Camera>();
    }

    private void SetShaderParameters(){
        RayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        RayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        RayTracingShader.SetFloat("_Seed", Random.value);
        RayTracingShader.SetFloat("_GlassRefractIndex", _GlassRefractIndex);
        RayTracingShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);
        RayTracingShader.SetBuffer(0, "_Spheres", _sphereBuffer);
        RayTracingShader.SetVector("_PixelOffset", new Vector2(Random.value, Random.value));
    }

    private void Update(){
        if(transform.hasChanged){
            _currentSample = 0;
            transform.hasChanged = false;
        }

        if(prevIndex != _GlassRefractIndex){
            prevIndex = _GlassRefractIndex;
            _currentSample = 0;
        }
    }
}
