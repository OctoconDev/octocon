defmodule Octocon.Accounts do
  @moduledoc """
  The Accounts context.

  This module represents the data layer for working with user accounts. Almost all operations
  require a system "identity", which is a tuple of the form:

  - `{:system, system_id}`: References a user by their system ID (7-character, alphanumeric lowercase string).
  - `{:discord, discord_id}`: References a user by their Discord ID.
  """

  import Ecto.Query, warn: false

  alias Octocon.Accounts.{
    Field,
    User
  }

  alias Octocon.Alters.Alter
  alias Octocon.Repo

  alias OctoconWeb.SystemJSON

  @doc """
  Returns a list of **all** users. This is a dangerous, long-running operation and should be used with caution.
  """
  def list_users do
    Repo.all(User)
  end

  defp unwrap_system_identity_where(system_identity, extra \\ []) do
    case system_identity do
      {:system, system_id} -> [id: system_id] |> Keyword.merge(extra)
      {:discord, discord_id} -> [discord_id: discord_id] |> Keyword.merge(extra)
    end
  end

  @doc """
  Returns the total number of users in the database.
  """
  def count do
    Repo.aggregate(User, :count)
  end

  @doc """
  Given a system identity, returns the desired ID. This does not query the database if the desired ID type was already given.

  ## Examples

      iex> id_from_system_identity({:system, "abcdefg"}, :system)
      "abcdefg"

      iex> id_from_system_identity({:discord, "123456789"}, :discord)
      "123456789"

      iex> id_from_system_identity({:system, "abcdefg"}, :discord)
      "123456789" # This will **query the database** for the Discord ID of the system with the ID "abcdefg".

      iex> id_from_system_identity({:discord, "123456789"}, :system)
      "abcdefg" # This will **query the database** for the system ID of the user with the Discord ID "123456789".
  """
  def id_from_system_identity(system_identity, type)

  def id_from_system_identity({:system, system_id}, :system), do: system_id
  def id_from_system_identity({:discord, discord_id}, :discord), do: discord_id

  def id_from_system_identity(system_identity, type) do
    where = unwrap_system_identity_where(system_identity)

    select =
      case type do
        :system -> fn query -> select(query, [u], u.id) end
        :discord -> fn query -> select(query, [u], u.discord_id) end
      end

    query =
      User
      |> where(^where)
      |> then(select)

    Repo.one(query)
  end

  @doc """
  Given a system identity, returns the `Octocon.Accounts.User` struct associated with it. Returns `nil` if no user is found.
  """
  def get_user(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)

    Repo.one(query)
  end

  @doc """
  Given a system identity, returns the user associated with it. Raises an `Ecto.NoResultsError` if no user is found.
  """
  def get_user!(system_identity) do
    case get_user(system_identity) do
      nil -> raise Ecto.NoResultsError
      user -> user
    end
  end

  @doc """
  Given a username, returns the `Octocon.Accounts.User` struct associated with it. Returns `nil` if no user is found.
  """
  def get_user_by_username(username) do
    query =
      from u in User,
        where: u.username == ^username

    Repo.one(query)
  end

  @doc """
  Given a username, returns the ID of the user associated with it. Returns `nil` if no user is found.
  """
  def get_user_id_by_username(username) do
    query =
      from u in User,
        where: u.username == ^username,
        select: u.id

    Repo.one(query)
  end

  @doc """
  Creates a user given the provided `email` address and extra `attrs`.
  """
  def create_user_from_email(email, attrs \\ %{}) do
    email
    |> User.create_from_email_changeset(attrs)
    |> Repo.insert()
  end

  @doc """
  Creates a user given the provided `discord_id` and extra `attrs`.
  """
  def create_user_from_discord(discord_id, attrs \\ %{}) do
    OctoconDiscord.ProxyCache.invalidate(discord_id)

    discord_id
    |> User.create_from_discord_changeset(attrs)
    |> Repo.insert()
  end

  @doc """
  Links a Discord ID to an existing user.

  This will return:
  - {:error, changeset} when the Discord ID is already linked to another account
  - {:error, :already_linked} when the user is already linked to a Discord account
  """
  def link_discord_to_user(%User{} = user, discord_id) do
    cond do
      user.discord_id != nil ->
        {:error, :already_linked}

      true ->
        user
        |> User.update_changeset(%{discord_id: discord_id})
        |> Repo.update()
        |> case do
          {:ok, value} ->
            OctoconDiscord.ProxyCache.invalidate(discord_id)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "discord_account_linked", %{
                discord_id: discord_id
              })
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def link_discord_to_user(system_identity, discord_id) do
    user = get_user!(system_identity)
    link_discord_to_user(user, discord_id)
  end

  @doc """
  Links an email to an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, changeset} when the email is already linked to another account.
  - {:error, :already_linked} when the user is already linked to an email.
  - {:error, :cannot_unlink} when the user is not linked to a Discord account (all accounts must be linked to at least one authentication method).
  """
  def link_email_to_user(%User{} = user, email) do
    cond do
      user.email != nil ->
        {:error, :already_linked}

      true ->
        user
        |> User.update_changeset(%{email: email})
        |> Repo.update()
        |> case do
          {:ok, value} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "google_account_linked", %{
                email: email
              })
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def link_email_to_user(system_identity, email) do
    user = get_user!(system_identity)
    link_email_to_user(user, email)
  end

  @doc """
  Unlinks an email from an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, :not_linked} when the user is not linked to an email.
  - {:error, :cannot_unlink} when the user is not linked to a Discord account (all accounts must be linked to at least one authentication method).
  """
  def unlink_email_from_user(%User{} = user) do
    cond do
      user.email == nil ->
        {:error, :not_linked}

      user.discord_id == nil ->
        {:error, :cannot_unlink}

      true ->
        user
        |> User.update_changeset(%{email: nil})
        |> Repo.update()
        |> case do
          {:ok, value} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "google_account_unlinked", %{})
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def unlink_email_from_user(system_identity) do
    user = get_user!(system_identity)
    unlink_email_from_user(user)
  end

  @doc """
  Unlinks a Discord ID from an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, :not_linked} when the user is not linked to a Discord account.
  - {:error, :cannot_unlink} when the user is not linked to an email (all accounts must be linked to at least one authentication method).
  """
  def unlink_discord_from_user(%User{} = user) do
    cond do
      user.discord_id == nil ->
        {:error, :not_linked}

      user.email == nil ->
        {:error, :cannot_unlink}

      true ->
        user
        |> User.update_changeset(%{discord_id: nil})
        |> Repo.update()
        |> case do
          {:ok, value} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "discord_account_unlinked", %{})
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def unlink_discord_from_user(system_identity) do
    user = get_user!(system_identity)
    unlink_discord_from_user(user)
  end

  @doc """
  Updates a `Octocon.Accounts.User` struct with the provided `attrs`.
  """
  def update_user(%User{} = user, attrs) do
    user
    |> User.update_changeset(attrs)
    |> Repo.update()
    |> case do
      {:ok, value} ->
        spawn(fn ->
          OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "self_updated", %{
            data: SystemJSON.data_me(value)
          })
        end)

        {:ok, value}

      {:error, changeset} ->
        {:error, changeset}
    end
  end

  @doc """
  Updates a user given the provided system identity and extra `attrs`.

  Raises when the changeset is invalid.
  """
  def update_user!(%User{} = user, attrs) do
    user
    |> User.update_changeset(attrs)
    |> Repo.update!()
  end

  @doc """
  Updates a user given the provided system identity and extra `attrs`.
  """
  def update_user_by_system_identity(system_identity, attrs) do
    user = get_user(system_identity)
    update_user(user, attrs)
  end

  @doc """
  Returns the primary front of the user associated with the provided system identity. May be `nil` if no primary front is set.
  """
  def get_primary_front(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> select([u], u.primary_front)

    Repo.one(query)
  end

  @doc """
  Returns the primary front, system tag, and whether the system tag should be shown of the user associated with the provided system identity.
  """
  def get_proxy_data_bulk(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> select([u], [
        :primary_front,
        :system_tag,
        :show_system_tag,
        :case_insensitive_proxying,
        :show_proxy_pronouns
      ])

    Repo.one(query)
  end

  @doc """
  Sets the primary front of the user associated with the provided system identity. May be `nil` to unset the primary front. Accepts
  either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the primary front is properly updated in the Discord cache.
  """
  def set_primary_front(identifier, alter_id)

  def set_primary_front(%User{} = user, alter_id) do
    result = update_user(user, %{primary_front: alter_id || nil})

    if match?({:ok, _}, result) do
      spawn(fn ->
        OctoconDiscord.ProxyCache.update_primary_front(user.discord_id, alter_id || nil)

        OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "primary_front", %{
          alter_id: alter_id || nil
        })
      end)
    end

    result
  end

  def set_primary_front(system_identity, alter_id) do
    user = get_user!(system_identity)

    set_primary_front(user, alter_id)
  end

  @doc """
  Sets the system tag of a user. Accepts either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the system tag is properly updated in the Discord cache.
  """
  def set_system_tag(identifier, system_tag)

  def set_system_tag(%User{} = user, system_tag) do
    result = update_user(user, %{system_tag: system_tag})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.update_system_tag(user.discord_id, system_tag)
    end

    result
  end

  def set_system_tag(system_identity, system_tag) do
    user = get_user!(system_identity)

    set_system_tag(user, system_tag)
  end

  @doc """
  Sets whether a user's system tag should be shown. Accepts either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the system tag visibility is properly updated in the Discord cache.
  """
  def set_show_system_tag(identifier, show_system_tag)

  def set_show_system_tag(%User{} = user, show_system_tag) do
    result = update_user(user, %{show_system_tag: show_system_tag})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.update_show_system_tag(user.discord_id, show_system_tag)
    end

    result
  end

  def set_show_system_tag(system_identity, show_system_tag) do
    user = get_user!(system_identity)

    set_show_system_tag(user, show_system_tag)
  end

  @doc """
  Sets whether a user should use case-insensitive proxying. Accepts either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the case-insensitive proxying setting is properly updated in the Discord cache.
  """
  def set_case_insensitive_proxying(identifier, case_insensitive_proxying)

  def set_case_insensitive_proxying(%User{} = user, case_insensitive_proxying) do
    result = update_user(user, %{case_insensitive_proxying: case_insensitive_proxying})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.update_case_insensitive_proxying(
        user.discord_id,
        case_insensitive_proxying
      )
    end

    result
  end

  def set_case_insensitive_proxying(system_identity, case_insensitive_proxying) do
    user = get_user!(system_identity)

    set_case_insensitive_proxying(user, case_insensitive_proxying)
  end

  @doc """
  Sets whether a user's proxy should automatically show pronouns as part of the username string.
  Accepts either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the proxy pronouns setting is properly updated in the Discord cache.
  """
  def set_show_proxy_pronouns(identifier, show_proxy_pronouns)

  def set_show_proxy_pronouns(%User{} = user, show_proxy_pronouns) do
    result = update_user(user, %{show_proxy_pronouns: show_proxy_pronouns})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.update_show_proxy_pronouns(user.discord_id, show_proxy_pronouns)
    end

    result
  end

  def set_show_proxy_pronouns(system_identity, show_proxy_pronouns) do
    user = get_user!(system_identity)

    set_show_proxy_pronouns(user, show_proxy_pronouns)
  end

  @doc """
  Sets whether alter IDs and aliases should automatically turn into proxies. Accepts either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the IDs as proxies setting is properly updated in the Discord cache.
  """
  def set_ids_as_proxies(identifier, ids_as_proxies)

  def set_ids_as_proxies(%User{} = user, ids_as_proxies) do
    result = update_user(user, %{ids_as_proxies: ids_as_proxies})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.update_ids_as_proxies(user.discord_id, ids_as_proxies)
    end

    result
  end

  def set_ids_as_proxies(system_identity, ids_as_proxies) do
    user = get_user!(system_identity)

    set_ids_as_proxies(user, ids_as_proxies)
  end

  @doc """
  Deletes the user associated with the provided system identity.
  """
  def delete_user(system_identity) do
    user = get_user!(system_identity)

    case Repo.delete(user) do
      {:error, _} ->
        {:error, :not_deleted}

      {:ok, _} ->
        OctoconDiscord.ProxyCache.invalidate(user.discord_id)
        :ok
    end
  end

  @doc false
  def wipe_alters_internal(system_identity) do
    Repo.transaction(fn ->
      user = get_user!(system_identity)

      query =
        from a in Alter,
          where: a.user_id == ^user.id

      Repo.delete_all(query)

      user
      |> User.update_changeset(%{primary_front: nil, lifetime_alter_count: 0})
      |> Repo.update()
    end)
  end

  @doc """
  Wipes all alters associated with the user associated with the provided system identity.

  This also resets the user's lifetime alter count to 0, so the next alter will be assigned ID 1.
  """
  def wipe_alters(system_identity) do
    OctoconDiscord.ProxyCache.invalidate(system_identity)

    Fly.Postgres.rpc_and_wait(__MODULE__, :wipe_alters_internal, [system_identity])
  end

  @doc """
  Builds a changeset based on the given `Octocon.Accounts.User` struct and `attrs` to change.
  """
  def change_user(%User{} = user, attrs \\ %{}) do
    User.update_changeset(user, attrs)
  end

  @doc """
  Checks if a user exists given the provided system identity.
  """
  def user_exists?(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)

    Repo.exists?(query)
  end

  @doc """
  Checks if a user exists given the provided email address.
  """
  def user_exists_with_email?(email) do
    query =
      from u in User,
        where: u.email == ^email

    Repo.exists?(query)
  end

  @doc false
  def get_user_proxy_map_old(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> join(:inner, [u], a in assoc(u, :alters))
      |> select([u, a], {u.id, a.id, a.discord_proxies})

    results = Repo.all(query)

    Enum.reduce(results, %{}, fn {system_id, alter_id, proxies}, acc ->
      Enum.reduce(proxies, acc, fn proxy, map ->
        Map.put(map, proxy, {system_id, alter_id})
      end)
    end)
  end

  @doc false
  def get_user_proxy_map(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    # query =
    #   User
    #   |> where(^where)
    #   |> join(:inner, [u], a in assoc(u, :alters))
    #   |> select([u, a], {u.id, a.id, a.discord_proxies})

    query =
      User
      |> where(^where)
      |> join(:inner, [u], a in assoc(u, :alters))
      |> where([u, a], not is_nil(a.discord_proxies) and a.discord_proxies != [])
      |> group_by([u, a], u.id)
      |> select([u, a], %{
        user_id: u.id,
        proxies: fragment("array_agg((?, ?))", a.id, a.discord_proxies)
      })

    result = Repo.one(query)

    new_proxies =
      result.proxies
      |> Enum.reduce(%{}, fn {alter_id, proxies}, acc ->
        Enum.reduce(proxies, acc, fn proxy, map ->
          Map.put(map, proxy, alter_id)
        end)
      end)

    %{result | proxies: new_proxies}
  end

  @doc """
  Wipes all custom fields for the user with the provided system identity.
  """
  def wipe_fields(system_identity) do
    user = get_user!(system_identity)

    user
    |> User.update_changeset()
    |> Ecto.Changeset.put_embed(:fields, [])
    |> Repo.update()
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Edits an existing custom field for the user with the provided system identity.
  """
  def edit_field(system_identity, id, data) do
    user = get_user!(system_identity)

    fields =
      user.fields
      |> Enum.map(fn field ->
        if field.id == id do
          Field.changeset(field, data)
        else
          field
        end
      end)

    user
    |> User.update_changeset()
    |> Ecto.Changeset.put_embed(:fields, fields)
    |> Repo.update()
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Adds a new custom field to the user with the provided system identity.
  """
  def add_field(system_identity, data) do
    user = get_user!(system_identity)
    fields = user.fields ++ [Field.changeset(nil, data)]

    user
    |> User.update_changeset()
    |> Ecto.Changeset.put_embed(:fields, fields)
    |> Repo.update()
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Removes an existing custom field from the user with the provided system identity.
  """
  def remove_field(system_identity, id) do
    user = get_user!(system_identity)

    fields =
      user.fields
      |> Enum.reject(fn field -> field.id == id end)

    user
    |> User.update_changeset()
    |> Ecto.Changeset.put_embed(:fields, fields)
    |> Repo.update()
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Relocates an existing custom field for the user with the provided system identity to the desired `index`.
  """
  def relocate_field(system_identity, id, index) do
    user = get_user!(system_identity)

    old_fields = user.fields
    field = Enum.find(old_fields, fn field -> field.id == id end)

    fields =
      old_fields
      |> Enum.reject(&(&1.id == id))
      |> List.insert_at(index, field)

    user
    |> User.update_changeset()
    |> Ecto.Changeset.put_embed(:fields, fields)
    |> Repo.update()
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Returns the custom fields for the user with the provided system identity.
  """
  def get_user_fields(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> select([u], u.fields)

    Repo.one(query)
  end

  defp wrap_fields_broadcast({:ok, _} = result, system_identity) do
    spawn(fn ->
      user_id = id_from_system_identity(system_identity, :system)

      OctoconWeb.Endpoint.broadcast!("system:#{user_id}", "fields_updated", %{
        fields:
          get_user_fields(system_identity) |> Enum.map(&Map.drop(&1, [:__meta__, :__struct__]))
      })
    end)

    result
  end

  defp wrap_fields_broadcast({:error, _} = result, _), do: result
end
