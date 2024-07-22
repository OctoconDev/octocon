defmodule Octocon.Global.FrontNotifier do
  @moduledoc false
  alias Octocon.Fronts

  use GenServer

  require Logger

  @via {:via, Horde.Registry, {Octocon.Primary.HordeRegistry, __MODULE__}}

  def start_link([]) do
    case GenServer.start_link(__MODULE__, [], name: @via) do
      {:ok, pid} ->
        {:ok, pid}

      {:error, {:already_started, pid}} ->
        Logger.info("Global.FrontNotifier already started at #{inspect(pid)}, returning :ignore")
        :ignore
    end
  end

  def add(system_id, alter_id) do
    GenServer.call(@via, {:add, system_id, alter_id})
  end

  def remove(system_id, alter_id) do
    GenServer.call(@via, {:remove, system_id, alter_id})
  end

  def set(system_id, alter_id) do
    GenServer.call(@via, {:set, system_id, alter_id})
  end

  @impl true
  def init([]) do
    Process.send_after(self(), :flush, :timer.seconds(5))
    {:ok, %{}}
  end

  @impl true
  def handle_call({:remove, system_id, alter_id}, _, state) do
    ensured = ensure_exists(system_id, state)

    {_, alters} = Map.get(ensured, system_id)

    {:reply, :ok,
     %{
       ensured
       | system_id => {
           get_time(),
           Enum.reject(alters, &(&1 == alter_id))
         }
     }}
  end

  @impl true
  def handle_call({:add, system_id, alter_id}, _, state) do
    ensured = ensure_exists(system_id, state)

    {_, alters} = Map.get(ensured, system_id)

    {:reply, :ok,
     %{
       ensured
       | system_id => {
           get_time(),
           [alter_id | alters]
         }
     }}
  end

  @impl true
  def handle_call({:set, system_id, alter_id}, _, state) do
    {:reply, :ok,
     Map.put(state, system_id, {
       get_time(),
       [alter_id]
     })}
  end

  @impl true
  def handle_info(:flush, state) do
    current_time = get_time()

    {evict, keep} =
      state
      |> Enum.reduce({[], []}, fn {system_id, {time, alters}}, {evict, keep} ->
        # If the alters have been in the state for more than 10 seconds
        if current_time - time > 10_000 do
          {[{system_id, alters} | evict], keep}
        else
          {evict, [{system_id, {time, alters}} | keep]}
        end
      end)

    Enum.each(evict, fn {system_id, alters} ->
      Octocon.FCM.push_friends_alters(system_id, alters)
    end)

    new_state = Enum.into(keep, %{})

    Process.send_after(self(), :flush, :timer.seconds(5))

    {:noreply, new_state}
  end

  defp ensure_exists(system_id, state) do
    if Map.has_key?(state, system_id) do
      state
    else
      Map.put(state, system_id, {get_time(), Fronts.currently_fronting_ids({:system, system_id})})
    end
  end

  defp get_time, do: :os.system_time(:millisecond)
end
